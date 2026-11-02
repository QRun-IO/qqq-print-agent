/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

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

   private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

   private void SignalPipeline()
   {
      lock(_stateLock)
      {
         TaskCompletionSource previous = _pipelineChanged;
         _pipelineChanged = NewSignal();
         previous.TrySetResult();
      }
   }

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

   private sealed class PipelineWave
   {
      public volatile bool Drained;
   }

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

   private void RegisterTicket(PrintJob job)
   {
      _printerTails.TryGetValue(job.PrinterId, out JobTicket? previous);
      var ticket = new JobTicket(previous?.Reserved.Task ?? Task.CompletedTask, previous?.Completed.Task ?? Task.CompletedTask);
      _jobTickets.Add(job.JobId, ticket);
      _printerTails[job.PrinterId] = ticket;
   }

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

   private sealed record JobTicket(Task PreviousReservation, Task PreviousCompletion)
   {
      public TaskCompletionSource Reserved { get; } = NewSignal();
      public TaskCompletionSource Completed { get; } = NewSignal();
   }

   private sealed class PipelineLease(Action release) : IAsyncDisposable
   {
      private Action? _release = release;
      public ValueTask DisposeAsync()
      {
         Interlocked.Exchange(ref _release, null)?.Invoke();
         return ValueTask.CompletedTask;
      }
   }
}
