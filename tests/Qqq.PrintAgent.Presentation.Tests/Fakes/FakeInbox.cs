/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.SignIn;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// A callback inbox a test delivers to by hand. Waiting ends when a callback is delivered, when a failure is injected, or
/// when the flow's token is cancelled, exactly like the real pipe inbox. Like the real one it finishes on whatever thread
/// delivered the callback, not on the caller's, so a flow that forgot to return to its own thread is caught by a test.
/// </summary>
internal sealed class FakeInbox : IBrowserCallbackInbox
{
   private readonly TaskCompletionSource<Uri> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private Task<Uri>? _wait;



   /// <summary>True once the flow closed the inbox.</summary>
   internal bool Disposed { get; private set; }



   /// <summary>True once the flow started waiting for a callback.</summary>
   internal bool Waiting { get; private set; }



   /// <summary>Completes when the flow starts waiting for a callback.</summary>
   internal Task Listening => _listening.Task;



   /// <summary>True once the flow's wait has ended, with a callback or otherwise. A delivery reaches the wait on another thread a moment later, so a test that must act after the callback arrived and before the flow continues waits for this.</summary>
   internal bool Received => _wait is { IsCompleted: true };



   /// <summary>Hands the flow the callback the browser was redirected to.</summary>
   internal void Deliver(Uri callback) => _callback.SetResult(callback);



   /// <summary>Makes the wait fail, as a broken inbox pipe or a malformed callback would.</summary>
   internal void Fail(Exception failure) => _callback.SetException(failure);



   /// <summary>Waits for a delivery, a failure or cancellation of the token.</summary>
   public Task<Uri> ReceiveAsync(CancellationToken cancellationToken)
   {
      Waiting = true;
      _listening.TrySetResult();
      _wait = _callback.Task.WaitAsync(cancellationToken);
      return _wait;
   }



   /// <summary>Marks the inbox closed.</summary>
   public void Dispose() => Disposed = true;
}
