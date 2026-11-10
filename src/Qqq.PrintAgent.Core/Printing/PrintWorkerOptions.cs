/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The hooks and limits the runtime hands a <see cref="PrintWorker"/>: the staging folder, the live server limits,
/// the claim and event gates, bandwidth throttling and the reservation callbacks, so concurrency and ordering are
/// decided by the runtime while the worker keeps the safety rules. Every one is required, because a worker without
/// any of them is not one the service runs; only the clock has a default.
/// </summary>
public sealed record PrintWorkerOptions
{
   /// <summary>Where documents are streamed to disk and verified before printing.</summary>
   public required string StagingDirectory { get; init; }



   /// <summary>The limits in force when the worker was created; a download is held to these.</summary>
   public required RealtimeSettings Settings { get; init; }



   /// <summary>Returns the limits in force right now, so a policy change reaches a job that is already past its first check.</summary>
   public required Func<RealtimeSettings> CurrentSettings { get; init; }



   /// <summary>Says whether a native submission may start; asked before and again after the SUBMITTING receipt is acknowledged, so a pause that arrives in between still stops it.</summary>
   public required Func<bool> CanSubmit { get; init; }



   /// <summary>Shared by every lane so pending events are delivered by one lane at a time and never twice.</summary>
   public required SemaphoreSlim EventGate { get; init; }



   /// <summary>Shared by every lane so only one claim is in flight until its RECEIVED event is acknowledged.</summary>
   public required SemaphoreSlim ClaimGate { get; init; }



   /// <summary>Paces every download against one shared bandwidth limit; given the bytes just received.</summary>
   public required Func<int, CancellationToken, Task> ThrottleDownloadAsync { get; init; }



   /// <summary>Says whether this computer is already working on the job with the given identifier, in any lane. A claim of such a job is a replay, and the worker leaves the job to the lane that holds it.</summary>
   public required Func<string, bool> IsInHand { get; init; }



   /// <summary>Says whether this lane may still claim; false once another lane found the queue empty.</summary>
   public required Func<bool> CanClaim { get; init; }



   /// <summary>Reserves staging space for a job before its download; the returned lease frees it when the job ends.</summary>
   public required Func<PrintJob, CancellationToken, Task<IAsyncDisposable>> ReserveDocumentAsync { get; init; }



   /// <summary>Takes the job's turn on its printer before native submission; the returned lease releases it when the job ends.</summary>
   public required Func<PrintJob, CancellationToken, Task<IAsyncDisposable>> AcquireSubmissionAsync { get; init; }



   /// <summary>Told which job the worker is processing, and null when it stops, so the runtime can track and cancel an active transfer.</summary>
   public required Action<PrintJob?> ActiveJobChanged { get; init; }



   /// <summary>The clock the per-job phase timings are measured on; the system clock unless a test supplies its own.</summary>
   public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
