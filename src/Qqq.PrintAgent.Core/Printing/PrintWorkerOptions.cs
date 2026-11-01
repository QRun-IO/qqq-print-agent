/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record PrintWorkerOptions
{
   public string? StagingDirectory { get; init; }
   public RealtimeSettings Settings { get; init; } = new();
   public Func<RealtimeSettings>? CurrentSettings { get; init; }
   public Func<bool>? CanSubmit { get; init; }
   public bool RecoverIncomplete { get; init; } = true;
   public bool SynchronizeInventory { get; init; } = true;
   public SemaphoreSlim? EventGate { get; init; }
   public SemaphoreSlim? ClaimGate { get; init; }
   public Func<int, CancellationToken, Task>? ThrottleDownloadAsync { get; init; }
   public Func<bool>? CanClaim { get; init; }
   public Func<PrintJob, CancellationToken, Task<IAsyncDisposable>>? ReserveDocumentAsync { get; init; }
   public Func<PrintJob, CancellationToken, Task<IAsyncDisposable>>? AcquireSubmissionAsync { get; init; }
   public Action<PrintJob?>? ActiveJobChanged { get; init; }
}
