/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Ties the print helper's life to the service that started it. The helper starts one watch before it reads its request
/// and stops it when its one operation is done; what the helper does in between is decided by
/// <see cref="PrinterWorkerHost"/>.
/// </summary>
internal interface IParentWatch
{
   /// <summary>
   /// Watches the process with the given id until the token is cancelled. If that process exits first, or does not exist,
   /// the helper must not go on running: the real watch ends this process at once. The returned task completes when the
   /// watch has stopped.
   /// </summary>
   Task WatchAsync(int parentId, CancellationToken token);
}
