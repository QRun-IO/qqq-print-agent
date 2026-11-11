/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// One browser exchange's claim on the callback inbox: the sign-in or provider sign-out that registered an OAuth state
/// with the <see cref="BrowserCallbackDispatcher"/> waits on this for the callback carrying that state. Disposing it
/// ends the claim, after which a callback with that state is dropped. The state is held only while the ticket waits
/// and is never shown. The ticket's task carries the callback, with its one-time code and state, so the ticket is
/// internal to the presentation assembly, whose sign-in and sign-out are the only ones to wait on it.
/// </summary>
internal sealed class BrowserCallbackTicket : IDisposable
{
   /// <summary>What the exchange waits on. Its continuations run asynchronously, so whoever awaits the ticket never continues inside the dispatcher, on the thread a callback arrived on.</summary>
   private readonly TaskCompletionSource<Uri> _callback = new(TaskCreationOptions.RunContinuationsAsynchronously);

   /// <summary>The dispatcher the ticket is registered with, which is told when the claim ends.</summary>
   private readonly BrowserCallbackDispatcher _dispatcher;



   /// <summary>Creates the ticket for a state; only the dispatcher does, when the state is registered.</summary>
   internal BrowserCallbackTicket(BrowserCallbackDispatcher dispatcher, string state)
   {
      _dispatcher = dispatcher;
      State = state;
   }



   /// <summary>The state this ticket waits for, which is how the dispatcher finds it.</summary>
   internal string State { get; }



   /// <summary>
   /// The callback carrying this ticket's state. It completes with the callback address when the dispatcher receives
   /// it, fails with the inbox's failure when the inbox failed while this ticket waited, and is cancelled when the
   /// ticket is disposed first. Whoever awaits it continues on its own thread, never inside the dispatcher.
   /// </summary>
   internal Task<Uri> Callback => _callback.Task;



   /// <summary>Hands the ticket its callback.</summary>
   internal void Complete(Uri callback) => _callback.TrySetResult(callback);



   /// <summary>
   /// Ends the wait with the inbox's failure, and marks the failure as seen. An exchange whose own wait had already
   /// ended (its token was cancelled) disposes the ticket without ever looking at the task, and the runtime reports
   /// a failed task nobody looked at as an unobserved error once it is collected. Whoever does await the ticket is
   /// given the failure all the same.
   /// </summary>
   internal void Fail(Exception failure)
   {
      if(_callback.TrySetException(failure))
      {
         _ = _callback.Task.Exception;
      }
   }



   /// <summary>Ends the claim: the dispatcher forgets the state, closes the inbox when no other ticket waits, and a wait that has no result yet is cancelled. Disposing a ticket that already has its callback changes nothing.</summary>
   public void Dispose()
   {
      _dispatcher.Release(this);
      _callback.TrySetCanceled();
   }
}
