/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Claim pipeline part of the runtime. A wave of lanes drains the server queue, and each job passes through ordered
/// gates: a download slot, a staging reservation, then a print slot for its printer. A ticket per job keeps jobs for one
/// printer in order. Shared counters change only under the state lock, and every release signals the lanes waiting on a change.
/// </summary>
public sealed partial class AgentRuntime
{
   /// <summary>
   /// How many lanes one wake-up starts. It equals the largest download concurrency the client advertises, so a wave can use
   /// every slot the server may grant; lanes beyond the granted slots wait for one, or stop once the queue is drained.
   /// </summary>
   private const int DrainLanes = 16;

   private readonly ChangeSignal _pipelineChanged = new();
   private readonly Dictionary<string, CancellationTokenSource> _activeTransfers = new(StringComparer.Ordinal);
   private readonly HashSet<string> _printingQueues = new(StringComparer.Ordinal);
   private readonly HashSet<string> _submittingJobs = new(StringComparer.Ordinal);
   private readonly SemaphoreSlim _claimGate = new(1, 1);
   private readonly Dictionary<string, JobTicket> _jobTickets = new(StringComparer.Ordinal);
   private readonly Dictionary<string, JobTicket> _printerTails = new(StringComparer.Ordinal);
   private long _stagedBytes;
   private int _activePrints;
   private int _activeDownloads;
   private readonly long _bandwidthOrigin = (timeProvider ?? TimeProvider.System).GetTimestamp();
   private double _bandwidthDue;
   private bool _transitioning;
   private CancellationTokenSource? _pipelineLifetime;
   private Task _pipelineTask = Task.CompletedTask;



   /// <summary>
   /// Long-running claim loop. At start it removes staging files a crash left behind, refusing reparse points. Each
   /// wake-up then starts a wave of drain lanes that share the work until the queue is empty or capacity is full. The
   /// wave is cancelled when the connection is replaced, after which the loop waits for the next wake-up.
   /// </summary>
   private async Task RealtimeDrainAsync(CancellationToken token)
   {
      string staging = Path.Combine(directory, "staging");
      Directory.CreateDirectory(staging);
      foreach(string abandoned in Directory.EnumerateFiles(staging, "*.document"))
      {
         if(File.GetAttributes(abandoned).HasFlag(FileAttributes.ReparsePoint))
         { throw new IOException("Untrusted staging file."); }
         File.Delete(abandoned);
      }
      while(!token.IsCancellationRequested)
      {
         await _claimsRequested.WaitAsync(token).ConfigureAwait(false);
         if(!_realtimeReady || _transitioning)
         { continue; }
         using var wave = CancellationTokenSource.CreateLinkedTokenSource(token);
         Task work;
         lock(_stateLock)
         {
            if(_transitioning || !_realtimeReady)
            { continue; }
            _pipelineLifetime = wave;
            var state = new PipelineWave();
            work = Task.WhenAll(Enumerable.Range(0, DrainLanes).Select(_ => Task.Run(() => DrainLaneAsync(staging, state, wave.Token), wave.Token)));
            _pipelineTask = work;
         }
         try
         { await work.ConfigureAwait(false); }
         catch(OperationCanceledException) when(wave.IsCancellationRequested && !token.IsCancellationRequested) { }
         finally
         {
            lock(_stateLock)
            { _pipelineLifetime = null; }
            _pipelineChanged.Raise();
         }
      }
   }



   /// <summary>
   /// One lane of a wave: it repeats single passes until a pass says to stop, the wave is cancelled or the connection is
   /// being replaced. A failing pass ends the lane without stopping the service.
   /// </summary>
   private async Task DrainLaneAsync(string staging, PipelineWave wave, CancellationToken token)
   {
      while(!token.IsCancellationRequested && !_transitioning)
      {
         var held = new LaneJob();
         try
         {
            if(!await RunLaneOnceAsync(staging, wave, held, token).ConfigureAwait(false))
            { return; }
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            StopLane(wave, held, exception);
            return;
         }
      }
   }



   /// <summary>
   /// Ends a lane whose pass failed. A failure before the lane held a job (the claim itself, or delivering earlier receipts)
   /// says the server cannot be asked right now, so the wave is marked drained and the other lanes do not repeat the same
   /// failing request; it is logged once for the wave. A failure while a job was held concerns that job only: it is logged and
   /// the other lanes carry on.
   /// </summary>
   private void StopLane(PipelineWave wave, LaneJob held, Exception exception)
   {
      bool report = true;
      if(!held.Claimed)
      {
         lock(_stateLock)
         {
            report = !wave.Failed;
            wave.Failed = true;
            wave.Drained = true;
         }
         _pipelineChanged.Raise();
      }
      if(report)
      { _log.Write("ERROR", "Print lane stopped: " + exception.Message); }
   }



   /// <summary>
   /// One pass of a lane: acquire a download slot, then run the worker once to claim and process a single job with fresh
   /// store and API objects. A lane neither reports journal leftovers nor sends inventory, because the runtime does
   /// both itself. It returns false when the lane should stop: the wave has drained, the computer is not
   /// enrolled, or this pass found the queue empty, in which case it marks the wave drained so the others stop claiming.
   /// </summary>
   private async Task<bool> RunLaneOnceAsync(string staging, PipelineWave wave, LaneJob held, CancellationToken token)
   {
      await using IAsyncDisposable? lane = await AcquireDownloadLaneAsync(wave, token).ConfigureAwait(false);
      if(lane is null)
      { return false; }
      using AgentStore store = OpenStore(recover: false);
      if(store.LoadConfiguration() is null)
      { return false; }
      using PrintApiClient api = CreateApi();
      using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
      PrintJob? active = null;
      var options = new PrintWorkerOptions
      {
         StagingDirectory = staging,
         Settings = _realtimeSettings,
         CurrentSettings = () => _realtimeSettings,
         CanSubmit = () => NormalTrafficAllowed && !_transitioning,
         EventGate = _eventDeliveryGate,
         ClaimGate = _claimGate,
         ThrottleDownloadAsync = ThrottleDownloadAsync,
         CanClaim = () => !wave.Drained,
         ReserveDocumentAsync = ReserveDocumentAsync,
         AcquireSubmissionAsync = AcquireSubmissionAsync,
         ActiveJobChanged = job =>
         {
            lock(_stateLock)
            {
               if(active is not null)
               { CompleteTicket(active); _activeTransfers.Remove(active.JobId); }
               active = job;
               if(job is not null)
               { held.Claimed = true; RegisterTicket(job); _activeTransfers[job.JobId] = jobCancellation; }
            }
         },
      };
      ExposedPrinterBackend printers = new(new ProfiledPrinterBackend(_printers, store), store.GetExposedPrinterIds);
      bool processed = await new PrintWorker(api, printers, store, message => _log.Write("INFO", message),
         () => DispatchAllowed && !UpdatePaused && !_transitioning, () => _realtimeReady, options).RunOnceAsync(jobCancellation.Token).ConfigureAwait(false);
      if(!processed)
      {
         lock(_stateLock)
         { wave.Drained = true; }
         _pipelineChanged.Raise();
      }
      return processed;
   }



   /// <summary>
   /// Waits for one of the configured concurrent download slots and returns a lease for it, or null when the wave has
   /// drained or the runtime is transitioning. It waits on the pipeline signal, not a timer, and takes a slot only while ready.
   /// </summary>
   private async Task<IAsyncDisposable?> AcquireDownloadLaneAsync(PipelineWave wave, CancellationToken token)
   {
      while(true)
      {
         Task changed;
         lock(_stateLock)
         {
            if(wave.Drained || _transitioning)
            { return null; }
            if(_realtimeReady && _activeDownloads < _realtimeSettings.MaxConcurrentDownloads)
            {
               _activeDownloads++;
               return new ReleaseOnce(() => { lock(_stateLock) { _activeDownloads--; } _pipelineChanged.Raise(); });
            }
            changed = _pipelineChanged.NextChange;
         }
         await changed.WaitAsync(token).ConfigureAwait(false);
      }
   }



   /// <summary>State shared by the lanes of one wave: drained once a lane found no more work or could not ask for it, which the others read so they stop claiming; failed once that reason was a failure, so it is logged a single time.</summary>
   private sealed class PipelineWave
   {
      public volatile bool Drained;
      public bool Failed;
   }



   /// <summary>What one pass of a lane got hold of: whether it claimed a job, which decides what a failure of that pass means.</summary>
   private sealed class LaneJob
   {
      public bool Claimed;
   }



   /// <summary>
   /// Shares one bandwidth limit across every download. Each chunk reserves the time it should take at the configured
   /// rate on a single schedule, so the aggregate rate holds however many lanes download at once. A rate of zero means unlimited.
   /// Both the schedule and the wait are measured on the runtime's clock, in seconds since the runtime was created, and
   /// the wait is rounded to the clock's own unit instead of being cut short of it, so the pacing is exact on any clock
   /// and a test can let it pass without waiting.
   /// </summary>
   internal Task ThrottleDownloadAsync(int bytes, CancellationToken token)
   {
      double delay;
      lock(_stateLock)
      {
         long rate = _realtimeSettings.MaxDownloadBytesPerSecond;
         double now = Clock.GetElapsedTime(_bandwidthOrigin).TotalSeconds;
         if(rate == 0)
         { _bandwidthDue = now; return Task.CompletedTask; }
         _bandwidthDue = Math.Max(now, _bandwidthDue) + (double)bytes / rate;
         delay = _bandwidthDue - now;
      }
      return Task.Delay(TimeSpan.FromTicks((long)Math.Round(delay * TimeSpan.TicksPerSecond)), Clock, token);
   }



   /// <summary>
   /// Reserves staging space for a document before it is downloaded. It first waits for the previous job on the same
   /// printer to have reserved, which keeps one printer's jobs in order. A document larger than its format limit or the whole
   /// staging area is refused outright; otherwise the lane waits until enough space is free. The returned lease frees the bytes.
   /// </summary>
   private async Task<IAsyncDisposable> ReserveDocumentAsync(PrintJob job, CancellationToken token)
   {
      JobTicket ticket;
      lock(_stateLock)
      { ticket = _jobTickets[job.JobId]; }
      await ticket.PreviousReservation.WaitAsync(token).ConfigureAwait(false);
      while(true)
      {
         Task changed;
         lock(_stateLock)
         {
            if(job.ContentLength < 1 || job.ContentLength > _realtimeSettings.MaximumDocumentBytes(job.Format) || job.ContentLength > _realtimeSettings.MaxStagedBytes)
            {
               throw new InvalidDataException("Document exceeds the effective format or staging limit.");
            }
            if(_stagedBytes <= _realtimeSettings.MaxStagedBytes - job.ContentLength)
            {
               _stagedBytes += job.ContentLength;
               ticket.Reserved.TrySetResult();
               return new ReleaseOnce(() => { lock(_stateLock) { _stagedBytes -= job.ContentLength; } _pipelineChanged.Raise(); });
            }
            changed = _pipelineChanged.NextChange;
         }
         await changed.WaitAsync(token).ConfigureAwait(false);
      }
   }



   /// <summary>
   /// Admits one job to native printing. It waits for the previous job on the same printer to complete, then for traffic
   /// to be allowed, no transition, a free print slot and no other job printing on that queue. An update pause does not hold
   /// a job here: it stops new claims, and a job that was already claimed is finished so the update does not lose it. The
   /// lease frees the slot and queue and records the job as no longer submitting.
   /// </summary>
   private async Task<IAsyncDisposable> AcquireSubmissionAsync(PrintJob job, CancellationToken token)
   {
      Task predecessor;
      lock(_stateLock)
      { predecessor = _jobTickets[job.JobId].PreviousCompletion; }
      await predecessor.WaitAsync(token).ConfigureAwait(false);
      while(true)
      {
         Task changed;
         lock(_stateLock)
         {
            if(NormalTrafficAllowed && !_transitioning && _activePrints < _realtimeSettings.MaxConcurrentPrints && _printingQueues.Add(job.PrinterId))
            {
               _activePrints++;
               _submittingJobs.Add(job.JobId);
               return new ReleaseOnce(() =>
               {
                  lock(_stateLock)
                  { _activePrints--; _printingQueues.Remove(job.PrinterId); _submittingJobs.Remove(job.JobId); }
                  _pipelineChanged.Raise();
               });
            }
            changed = _pipelineChanged.NextChange;
         }
         await changed.WaitAsync(token).ConfigureAwait(false);
      }
   }



   /// <summary>Links a new job behind the newest job for the same printer, so its reservation and completion wait for that one.</summary>
   private void RegisterTicket(PrintJob job)
   {
      _printerTails.TryGetValue(job.PrinterId, out JobTicket? previous);
      var ticket = new JobTicket(previous?.Reserved.Task ?? Task.CompletedTask, previous?.Completed.Task ?? Task.CompletedTask);
      _jobTickets.Add(job.JobId, ticket);
      _printerTails[job.PrinterId] = ticket;
   }



   /// <summary>Releases both of the job's signals so its successor is never left waiting, and clears the printer's tail only when this job was still the newest.</summary>
   private void CompleteTicket(PrintJob job)
   {
      if(_jobTickets.Remove(job.JobId, out JobTicket? ticket))
      {
         ticket.Reserved.TrySetResult();
         ticket.Completed.TrySetResult();
         if(_printerTails.TryGetValue(job.PrinterId, out JobTicket? tail) && ReferenceEquals(ticket, tail))
         { _printerTails.Remove(job.PrinterId); }
      }
   }



   /// <summary>A job's place in its printer's order: the previous job's reservation and completion to wait for, and this job's own signals for its successor.</summary>
   private sealed record JobTicket(Task PreviousReservation, Task PreviousCompletion)
   {
      public TaskCompletionSource Reserved { get; } = ChangeSignal.OneShot();
      public TaskCompletionSource Completed { get; } = ChangeSignal.OneShot();
   }
}
