/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Owns the one browser callback inbox. Windows gives each user one redirect address and one inbox, so browser
/// exchanges cannot each have their own: a sign-in or a provider sign-out registers the OAuth state it sent and gets a
/// ticket to await, and this hands each callback to the ticket whose state it carries. Two servers can then be signing
/// in at once, and a late callback for an exchange that has ended is dropped instead of failing another. An inbox is
/// open exactly while at least one ticket waits. Each inbox takes one callback, so after every callback it is closed and,
/// when tickets still wait, a new one is opened. A callback that carries no state, more than one, or a state nobody
/// waits for is dropped, and the log says only that one was dropped: nothing of a callback, its state or its code is
/// ever logged, and a registered state is held only while its ticket waits. When the inbox itself fails, which
/// includes an inbox that refuses what it received as no callback at all, every waiting ticket fails and the next
/// registration opens a new inbox. The tickets are given the inbox's failure when it is of a kind every exchange
/// reports, and otherwise one that names only its kind (see <see cref="BrowserCallbackFailure"/>), so whatever an
/// inbox fails with ends each exchange as a failure it reports and never as an exception nobody handles. Tickets
/// are registered from the window's thread and callbacks arrive on others, so the waiting set is guarded, and this
/// never continues on a caller's thread. A log that fails does not stop any of it.
/// </summary>
public sealed class BrowserCallbackDispatcher
{
   private readonly IBrowserCallbackSource _callbacks;
   private readonly IPresentationLog _log;
   private readonly Lock _gate = new();
   private readonly Dictionary<string, BrowserCallbackTicket> _waiting = new(StringComparer.Ordinal);
   private IBrowserCallbackInbox? _inbox;
   private CancellationTokenSource? _listening;



   /// <summary>Wires the dispatcher to where callbacks arrive and to the log.</summary>
   public BrowserCallbackDispatcher(IBrowserCallbackSource callbacks, IPresentationLog log)
   {
      _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
      _log = log ?? throw new ArgumentNullException(nameof(log));
   }



   /// <summary>
   /// Registers the state a browser exchange sent and returns the ticket to await. The inbox is open when this
   /// returns, so the caller opens the browser afterwards and a fast redirect is not lost. A state that is already
   /// waiting is refused, because two exchanges could not be told apart. When the inbox cannot be opened the failure is
   /// thrown to the caller and nothing is registered; a failure of a kind the exchanges do not report is thrown as
   /// one that names only its kind, as it is for a ticket. The caller disposes the ticket when its exchange ends,
   /// however it ends.
   /// </summary>
   public BrowserCallbackTicket Register(string state)
   {
      ArgumentException.ThrowIfNullOrEmpty(state);
      BrowserCallbackTicket ticket = new(this, state);
      lock(_gate)
      {
         if(!_waiting.TryAdd(state, ticket))
         {
            throw new InvalidOperationException("A browser exchange with the same state is already waiting for its callback.");
         }
         if(_inbox is null)
         {
            try
            {
               (IBrowserCallbackInbox inbox, CancellationToken closed) = Open();
               _ = ReceiveAsync(inbox, closed);
            }
            catch(Exception exception) when(!BrowserCallbackFailure.Listed(exception))
            {
               _waiting.Remove(state);
               throw BrowserCallbackFailure.Reported(exception);
            }
            catch(Exception)
            {
               _waiting.Remove(state);
               throw;
            }
         }
      }
      return ticket;
   }



   /// <summary>
   /// Reads the one <c>state</c> parameter of an address's query: the state a sign-in sent, from the authorization
   /// address, or the state a callback returns. It is null when the address has none, an empty one or more than one,
   /// because a repeated parameter could make two readers disagree about which exchange a callback belongs to.
   /// </summary>
   internal static string? ReadState(Uri address)
   {
      string? state = null;
      if(!address.IsAbsoluteUri)
      {
         return null;
      }
      foreach(string entry in address.Query.TrimStart('?').Split('&'))
      {
         string[] pair = entry.Split('=', 2);
         if(pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0].Replace('+', ' ')), "state", StringComparison.Ordinal))
         {
            if(state is not null)
            {
               return null;
            }
            state = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
         }
      }
      return string.IsNullOrEmpty(state) ? null : state;
   }



   /// <summary>Ends a ticket's claim. Only the ticket that is waiting under its state is forgotten, so disposing a ticket that already has its callback cannot remove a later one, and the inbox is closed when nothing waits any more.</summary>
   internal void Release(BrowserCallbackTicket ticket)
   {
      lock(_gate)
      {
         bool waited = ((ICollection<KeyValuePair<string, BrowserCallbackTicket>>)_waiting).Remove(new KeyValuePair<string, BrowserCallbackTicket>(ticket.State, ticket));
         if(waited && _waiting.Count == 0)
         {
            Close();
         }
      }
   }



   /// <summary>
   /// Opens an inbox, makes it the open one, and returns it with the token that ends the wait on it when it is
   /// closed. The caller holds the gate, and waits on the inbox next: a registration starts the wait before it lets
   /// go of the gate, and the wait that has just routed a callback goes on with the inbox it is given. A failure to
   /// open is thrown, and no inbox is then open.
   /// </summary>
   private (IBrowserCallbackInbox Inbox, CancellationToken Closed) Open()
   {
      IBrowserCallbackInbox inbox = _callbacks.Open();
      CancellationTokenSource listening = new();
      _inbox = inbox;
      _listening = listening;
      Record(static log => log.Info("Browser callback inbox open."));
      return (inbox, listening.Token);
   }



   /// <summary>
   /// Writes a log entry in a way that cannot break the dispatcher. Its entries are written between steps that
   /// belong together: after an inbox was opened and before anyone waits on it, after an inbox was closed and before
   /// the next one is opened, before the tickets of a broken inbox are failed. A log that threw there would leave
   /// an inbox nobody reads, or tickets waiting for nothing until their own time limits ran out. So a failure of
   /// the log stays with the log: the entry is lost, and the dispatcher goes on.
   /// </summary>
   private void Record(Action<IPresentationLog> entry)
   {
      try
      {
         entry(_log);
      }
      catch(Exception)
      {
         // A log that cannot be written to must not cost an operator the sign-in that is waiting.
      }
   }



   /// <summary>
   /// Closes the open inbox. The caller holds the gate. The fields are cleared before the wait is cancelled, because
   /// cancelling can run the end of that wait at once, on this thread, and it must then find that its inbox is no
   /// longer the open one and do nothing.
   /// </summary>
   private void Close()
   {
      IBrowserCallbackInbox inbox = _inbox!;
      CancellationTokenSource listening = _listening!;
      _inbox = null;
      _listening = null;
      listening.Cancel();
      inbox.Dispose();
      listening.Dispose();
   }



   /// <summary>
   /// Waits for the one callback of an inbox, routes it, and goes on with the inbox that was opened for the tickets
   /// that still wait, until none is. It is one loop for a whole run of inboxes, not a call per inbox: an inbox
   /// whose callback is already there when the wait starts is dealt with at once, and a call per inbox would then
   /// go one level deeper for every such callback. Any failure of the wait, whatever its kind, is the inbox
   /// failing, and has to reach the tickets that wait: a failure left here would leave every one of them waiting for
   /// its own time limit. The wait that ends because the inbox was closed is told apart inside
   /// <see cref="Fail"/>, by the inbox no longer being the open one.
   /// </summary>
   private async Task ReceiveAsync(IBrowserCallbackInbox inbox, CancellationToken closed)
   {
      IBrowserCallbackInbox? open = inbox;
      while(open is not null)
      {
         Uri callback;
         try
         {
            callback = await open.ReceiveAsync(closed).ConfigureAwait(false);
         }
         catch(Exception exception)
         {
            Fail(open, exception);
            return;
         }
         open = Route(open, callback, out closed);
      }
   }



   /// <summary>
   /// Hands a received callback to the ticket whose state it carries, or drops it. The inbox that delivered it is
   /// closed and, when tickets still wait, the next one is opened in the same step, so an inbox is open exactly while
   /// a ticket waits and the gap between two inboxes is as short as it can be. That next inbox is returned, with the
   /// token that ends the wait on it, for the caller to wait on; null means nothing is left to wait for, or another
   /// wait already has the open inbox. When the next inbox cannot be opened, the tickets that wait cannot receive
   /// anything and are failed. A callback from an inbox that was closed meanwhile (its last ticket ended as the
   /// callback arrived) is dropped. The log line for a drop is fixed text, written before the next inbox opens so
   /// the log reads in the order things happened. The ticket is completed last, outside the gate.
   /// </summary>
   private IBrowserCallbackInbox? Route(IBrowserCallbackInbox inbox, Uri callback, out CancellationToken closed)
   {
      BrowserCallbackTicket? ticket = null;
      BrowserCallbackTicket[] stranded = [];
      Exception? failure = null;
      IBrowserCallbackInbox? next = null;
      closed = CancellationToken.None;
      lock(_gate)
      {
         if(ReferenceEquals(_inbox, inbox))
         {
            Close();
            if(ReadState(callback) is { } state)
            {
               _waiting.Remove(state, out ticket);
            }
         }
         if(ticket is null)
         {
            Record(static log => log.Warning("A browser callback that belongs to no waiting sign-in or sign-out was dropped. Nothing of it is logged."));
         }
         if(_inbox is null && _waiting.Count != 0)
         {
            try
            {
               (next, closed) = Open();
            }
            catch(Exception exception)
            {
               stranded = TakeWaiting();
               failure = exception;
            }
         }
      }
      if(failure is not null)
      {
         End(stranded, failure);
      }
      ticket?.Complete(callback);
      return next;
   }



   /// <summary>Handles a wait that ended without a callback. When the inbox is still the open one it failed: it is closed and every waiting ticket fails. Otherwise it was closed on purpose and nothing waits on it.</summary>
   private void Fail(IBrowserCallbackInbox inbox, Exception failure)
   {
      BrowserCallbackTicket[] stranded;
      lock(_gate)
      {
         if(!ReferenceEquals(_inbox, inbox))
         {
            return;
         }
         Close();
         stranded = TakeWaiting();
      }
      End(stranded, failure);
   }



   /// <summary>Removes and returns every waiting ticket. The caller holds the gate.</summary>
   private BrowserCallbackTicket[] TakeWaiting()
   {
      BrowserCallbackTicket[] waiting = [.. _waiting.Values];
      _waiting.Clear();
      return waiting;
   }



   /// <summary>
   /// Fails the tickets a broken inbox left waiting, each once, and logs the kind of failure and how many it ended.
   /// The tickets get the failure itself when it is of a kind every exchange reports, whose text is then left to
   /// each exchange to report, and otherwise one that names only its kind. Nothing an inbox put in a message is
   /// logged from here.
   /// </summary>
   private void End(BrowserCallbackTicket[] stranded, Exception failure)
   {
      Record(log => log.Error("The browser callback inbox failed ({Failure}); {Waiting} waiting browser exchange(s) were ended and have to be started again.", failure.GetType().Name, stranded.Length));
      Exception reported = BrowserCallbackFailure.Reported(failure);
      foreach(BrowserCallbackTicket ticket in stranded)
      {
         ticket.Fail(reported);
      }
   }
}
