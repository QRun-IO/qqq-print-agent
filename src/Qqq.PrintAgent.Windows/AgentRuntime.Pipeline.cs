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
   private TaskCompletionSource _pipelineChanged = NewSignal();
   private readonly Dictionary<string, CancellationTokenSource> _activeTransfers = new(StringComparer.Ordinal);
   private readonly HashSet<string> _printingQueues = new(StringComparer.Ordinal);
   private readonly HashSet<string> _submittingJobs = new(StringComparer.Ordinal);
   private readonly SemaphoreSlim _claimGate = new(1, 1);
   private readonly Dictionary<string, JobTicket> _jobTickets = new(StringComparer.Ordinal);
   private readonly Dictionary<string, JobTicket> _printerTails = new(StringComparer.Ordinal);
   private long _stagedBytes;
   private int _activePrints;
   private int _activeDownloads;
   private double _bandwidthDue;
   private bool _transitioning;
   private CancellationTokenSource? _pipelineLifetime;
   private Task _pipelineTask = Task.CompletedTask;



   /// <summary>Creates a one-shot signal whose continuations run asynchronously, so completing it under a lock never runs waiter code inside that lock.</summary>
   private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);



   /// <summary>
   /// Wakes everything waiting for a change, such as freed capacity or changed readiness. A fresh signal is swapped in
   /// under the state lock and the old one completed, so a waiter holding the old signal is released and a later arrival
   /// waits for the next change instead of missing it.
   /// </summary>
   private void SignalPipeline()
   {
      lock(_stateLock)
      {
         TaskCompletionSource previous = _pipelineChanged;
         _pipelineChanged = NewSignal();
         previous.TrySetResult();
      }
   }



   /// <summary>
   /// Long-running claim loop. At start it removes staging files a crash left behind, refusing reparse points. Each
   /// wake-up then starts a wave of 16 drain lanes that share the work until the queue is empty or capacity is full. The
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
         await _pollRequested.WaitAsync(token).ConfigureAwait(false);
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
            work = Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => DrainLaneAsync(staging, state, wave.Token), wave.Token)));
            _pipelineTask = work;
         }
         try
         { await work.ConfigureAwait(false); }
         catch(OperationCanceledException) when(wave.IsCancellationRequested && !token.IsCancellationRequested) { }
         finally
         {
            lock(_stateLock)
            { _pipelineLifetime = null; }
            SignalPipeline();
         }
      }
   }



   /// <summary>
   /// One lane of a wave: acquire a download slot, then run the worker once to claim and process a single job with fresh
   /// store and API objects. Realtime lanes neither recover journal leftovers nor resynchronize inventory, because the
   /// runtime does that once. The first lane to find the queue empty marks the wave drained so the others stop claiming,
   /// and a failing lane logs and ends without stopping the service.
   /// </summary>
   private async Task DrainLaneAsync(string staging, PipelineWave wave, CancellationToken token)
   {
      while(!token.IsCancellationRequested && !_transitioning)
      {
         try
         {
            await using IAsyncDisposable? lane = await AcquireDownloadLaneAsync(wave, token).ConfigureAwait(false);
            if(lane is null)
            { return; }
            using var store = new AgentStore(directory);
            AgentConfiguration? configuration = store.LoadConfiguration();
            if(configuration is null)
            { return; }
            using PrintApiClient api = CreateApi(configuration);
            using var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            PrintJob? active = null;
            var options = new PrintWorkerOptions
            {
               StagingDirectory = staging,
               Settings = _realtimeSettings,
               CurrentSettings = () => _realtimeSettings,
               CanSubmit = () => NormalTrafficAllowed && !UpdatePaused && !_transitioning,
               RecoverIncomplete = false,
               SynchronizeInventory = false,
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
                     { RegisterTicket(job); _activeTransfers[job.JobId] = jobCancellation; }
                  }
               },
            };
            ExposedPrinterBackend printers = new(new ProfiledPrinterBackend(_printers, store), store.GetExposedPrinterIds);
            bool processed = await new PrintWorker(api, printers, store, message => WriteLog("INFO", message),
               () => DispatchAllowed && !UpdatePaused && !_transitioning, () => _realtimeReady, options).RunOnceAsync(jobCancellation.Token).ConfigureAwait(false);
            if(!processed)
            {
               lock(_stateLock)
               { wave.Drained = true; }
               SignalPipeline();
               return;
            }
         }
         catch(Exception exception) when(exception is not OperationCanceledException || !token.IsCancellationRequested)
         {
            WriteLog("ERROR", "Print lane stopped: " + exception.Message);
            return;
         }
      }
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
               return new PipelineLease(() => { lock(_stateLock) { _activeDownloads--; } SignalPipeline(); });
            }
            changed = _pipelineChanged.Task;
         }
         await changed.WaitAsync(token).ConfigureAwait(false);
      }
   }



   /// <summary>State shared by the lanes of one wave: set when a lane found no more work, read by the others so they stop claiming.</summary>
   private sealed class PipelineWave
   {
      public volatile bool Drained;
   }



   /// <summary>
   /// Shares one bandwidth limit across every download. Each chunk reserves the time it should take at the configured
   /// rate on a single schedule, so the aggregate rate holds however many lanes download at once. A rate of zero means unlimited.
   /// </summary>
   private Task ThrottleDownloadAsync(int bytes, CancellationToken token)
   {
      double delay;
      lock(_stateLock)
      {
         long rate = _realtimeSettings.MaxDownloadBytesPerSecond;
         double now = (double)System.Diagnostics.Stopwatch.GetTimestamp() / System.Diagnostics.Stopwatch.Frequency;
         if(rate == 0)
         { _bandwidthDue = now; return Task.CompletedTask; }
         _bandwidthDue = Math.Max(now, _bandwidthDue) + (double)bytes / rate;
         delay = _bandwidthDue - now;
      }
      return Task.Delay(TimeSpan.FromSeconds(delay), token);
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
               return new PipelineLease(() => { lock(_stateLock) { _stagedBytes -= job.ContentLength; } SignalPipeline(); });
            }
            changed = _pipelineChanged.Task;
         }
         await changed.WaitAsync(token).ConfigureAwait(false);
      }
   }



   /// <summary>
   /// Admits one job to native printing. It waits for the previous job on the same printer to complete, then for traffic
   /// to be allowed, no update or transition, a free print slot and no other job printing on that queue. The lease frees
   /// the slot and queue and records the job as no longer submitting.
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
            if(NormalTrafficAllowed && !UpdatePaused && !_transitioning && _activePrints < _realtimeSettings.MaxConcurrentPrints && _printingQueues.Add(job.PrinterId))
            {
               _activePrints++;
               _submittingJobs.Add(job.JobId);
               return new PipelineLease(() =>
               {
                  lock(_stateLock)
                  { _activePrints--; _printingQueues.Remove(job.PrinterId); _submittingJobs.Remove(job.JobId); }
                  SignalPipeline();
               });
            }
            changed = _pipelineChanged.Task;
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
      public TaskCompletionSource Reserved { get; } = NewSignal();
      public TaskCompletionSource Completed { get; } = NewSignal();
   }



   /// <summary>Runs its release action exactly once however many times it is disposed, so a normal path and an error path cannot double-count a freed resource.</summary>
   private sealed class PipelineLease(Action release) : IAsyncDisposable
   {
      private Action? _release = release;



      /// <summary>Atomically takes the release action and runs it; later calls do nothing.</summary>
      public ValueTask DisposeAsync()
      {
         Interlocked.Exchange(ref _release, null)?.Invoke();
         return ValueTask.CompletedTask;
      }
   }
}
