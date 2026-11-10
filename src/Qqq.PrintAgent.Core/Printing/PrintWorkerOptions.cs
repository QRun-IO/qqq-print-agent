/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Optional hooks and limits that change how a <see cref="PrintWorker"/> runs. The defaults give the simple serial worker; the
/// realtime service supplies a staging folder, the live server limits, claim and event gates, bandwidth throttling, and
/// reservation callbacks, so concurrency and ordering are decided by the runtime while the worker keeps the safety rules.
/// </summary>
public sealed record PrintWorkerOptions
{
   /// <summary>Where documents are streamed to disk before printing. Null keeps the legacy in-memory download, which is limited to 20 MiB.</summary>
   public string? StagingDirectory { get; init; }



   /// <summary>The limits in force when the worker was created; used when no live callback is supplied.</summary>
   public RealtimeSettings Settings { get; init; } = new();



   /// <summary>Returns the limits in force right now, so a policy change reaches a job that is already past its first check.</summary>
   public Func<RealtimeSettings>? CurrentSettings { get; init; }



   /// <summary>Says whether a native submission may start. When absent the worker's dispatch check decides, so a pause still stops submission.</summary>
   public Func<bool>? CanSubmit { get; init; }



   /// <summary>False when the runtime already reported jobs a previous run left unfinished, so each lane only drains and does not report them again.</summary>
   public bool RecoverIncomplete { get; init; } = true;



   /// <summary>False when the runtime sends inventory itself, so a lane does not repeat it before every claim.</summary>
   public bool SynchronizeInventory { get; init; } = true;



   /// <summary>Shared by every lane so pending events are delivered by one lane at a time and never twice.</summary>
   public SemaphoreSlim? EventGate { get; init; }



   /// <summary>Shared by every lane so only one claim is in flight until its RECEIVED event is acknowledged.</summary>
   public SemaphoreSlim? ClaimGate { get; init; }



   /// <summary>Paces every download against one shared bandwidth limit; given the bytes just received.</summary>
   public Func<int, CancellationToken, Task>? ThrottleDownloadAsync { get; init; }



   /// <summary>Says whether this lane may still claim; false once another lane found the queue empty.</summary>
   public Func<bool>? CanClaim { get; init; }



   /// <summary>Reserves staging space for a job before its download; the returned lease frees it when the job ends.</summary>
   public Func<PrintJob, CancellationToken, Task<IAsyncDisposable>>? ReserveDocumentAsync { get; init; }



   /// <summary>Takes the job's turn on its printer before native submission; the returned lease releases it when the job ends.</summary>
   public Func<PrintJob, CancellationToken, Task<IAsyncDisposable>>? AcquireSubmissionAsync { get; init; }



   /// <summary>Told which job the worker is processing, and null when it stops, so the runtime can track and cancel an active transfer.</summary>
   public Action<PrintJob?>? ActiveJobChanged { get; init; }



   /// <summary>The clock the per-job phase timings are measured on; the system clock unless a test supplies its own.</summary>
   public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
