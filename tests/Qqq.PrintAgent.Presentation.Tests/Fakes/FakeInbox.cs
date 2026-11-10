/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.SignIn;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>A callback inbox a test delivers to by hand. Waiting ends when a callback is delivered, when a failure is injected, or when the flow's token is cancelled, exactly like the real pipe inbox.</summary>
internal sealed class FakeInbox : IBrowserCallbackInbox
{
   private readonly TaskCompletionSource<Uri> _callback = new();



   /// <summary>True once the flow closed the inbox.</summary>
   internal bool Disposed { get; private set; }



   /// <summary>True once the flow started waiting for a callback.</summary>
   internal bool Waiting { get; private set; }



   /// <summary>Hands the flow the callback the browser was redirected to.</summary>
   internal void Deliver(Uri callback) => _callback.SetResult(callback);



   /// <summary>Makes the wait fail, as a broken inbox pipe would.</summary>
   internal void Fail(Exception failure) => _callback.SetException(failure);



   /// <summary>Waits for a delivery, a failure or cancellation of the token.</summary>
   public async Task<Uri> ReceiveAsync(CancellationToken cancellationToken)
   {
      Waiting = true;
      await using CancellationTokenRegistration registration = cancellationToken.Register(() => _callback.TrySetCanceled(cancellationToken));
      return await _callback.Task;
   }



   /// <summary>Marks the inbox closed.</summary>
   public void Dispose() => Disposed = true;
}
