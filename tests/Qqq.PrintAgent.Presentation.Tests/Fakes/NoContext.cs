/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// Removes the test runner's synchronization context from the calling thread until it is disposed. With a context
/// present, .NET never continues an awaiting method inside the call that completes what it awaits; without one it
/// does, which is what happens on the thread-pool threads the callback dispatcher works on. A test uses this to make
/// that order of events certain: when the call that delivers something returns, whoever awaited it has dealt with it.
/// </summary>
internal sealed class NoContext : IDisposable
{
   private readonly SynchronizationContext? _before = SynchronizationContext.Current;



   /// <summary>Removes the context.</summary>
   internal NoContext() => SynchronizationContext.SetSynchronizationContext(null);



   /// <summary>Puts the context back.</summary>
   public void Dispose() => SynchronizationContext.SetSynchronizationContext(_before);
}
