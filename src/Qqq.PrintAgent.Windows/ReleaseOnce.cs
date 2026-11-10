/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// A lease on something counted, such as a download slot, staged bytes or a printer's turn. It runs its release action
/// exactly once however many times, and from however many threads, it is disposed, so a normal path and an error path
/// cannot double-count a freed resource.
/// </summary>
internal sealed class ReleaseOnce(Action release) : IAsyncDisposable
{
   private Action? _release = release ?? throw new ArgumentNullException(nameof(release));



   /// <summary>Atomically takes the release action and runs it; later calls do nothing.</summary>
   public ValueTask DisposeAsync()
   {
      Interlocked.Exchange(ref _release, null)?.Invoke();
      return ValueTask.CompletedTask;
   }
}
