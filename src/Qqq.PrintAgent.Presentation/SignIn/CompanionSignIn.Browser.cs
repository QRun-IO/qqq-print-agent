/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Browser sign-in: the exchange with the identity provider through the default browser, its time limit and
/// cancellation, and what is recorded when the browser never comes back. The callback is awaited through the one
/// <see cref="BrowserCallbackDispatcher"/>, by the state this attempt sent, so another server's sign-in can be waiting
/// at the same time and a callback that is not this attempt's never reaches it.
/// <para>
/// When an attempt keeps its session. An attempt that failed keeps it, whatever the failure and whenever it
/// happened. An attempt whose wait ended (the operator cancelled, the three minutes ran out, or the caller stopped
/// waiting) keeps it only when that happened before the callback was handed to the session: the session was given
/// nothing then, and the next attempt can use it without a new read. From the moment the callback is handed over,
/// the server may complete the sign-in on that session whatever happens here. An attempt that then ends without a
/// result discards the session, so that nothing here holds a signed-in session behind an attempt that was reported
/// as cancelled, and the next attempt needs a new read: <see cref="HasSession"/> is false and <see cref="Offer"/>
/// is null, while the state still says how the attempt ended (cancelled, timed out, or not prepared for a caller
/// that stopped waiting).
/// </para>
/// </summary>
public sealed partial class CompanionSignIn
{
   /// <summary>How long a browser sign-in waits before it is given up.</summary>
   public static readonly TimeSpan BrowserSignInLimit = TimeSpan.FromMinutes(3);

   /// <summary>How far back the protocol handler's trace is read when a browser sign-in ends without a result.</summary>
   private static readonly TimeSpan s_traceWindow = TimeSpan.FromMinutes(10);

   /// <summary>What the Cancel button cancels: set while a browser exchange runs, and cleared before the exchange's cancellation sources are released.</summary>
   private CancellationTokenSource? _browserCancel;

   /// <summary>True from the moment the running browser attempt hands its callback to the session until the attempt has ended. An attempt that ends without a result while this is set discards the session.</summary>
   private bool _handedOver;

   /// <summary>What the running browser attempt has not got past on its way to opening the provider's page, in words for the log; null once the page is open. An attempt that ends while this is set never involved a browser.</summary>
   private string? _unopened;



   /// <summary>
   /// Signs in through the browser, with a three minute limit, and returns the session credential. Nothing happens
   /// without a session that offers the browser. The redirect address is the one this application's installer
   /// registered, and it is used only when the server also lists it, so a server cannot steer the callback elsewhere.
   /// A cancelled, timed-out or failed attempt is reported by state. A failed attempt keeps the session, and so does
   /// one that was cancelled or timed out before its callback was handed to the session, so the operator can try
   /// again: a late callback of the abandoned attempt carries a state nobody waits for and is dropped by the
   /// dispatcher. One that was cancelled or timed out after the callback was handed over discards the session (the
   /// type's summary says when a session is kept). It returns null when no credential was obtained.
   /// </summary>
   public async Task<QqqSessionCredential?> SignInWithBrowserAsync(CancellationToken cancellationToken = default)
   {
      if(_session is not { } session || _offer is not { Browser: true } || !TryBegin("Browser sign-in"))
      {
         return null;
      }
      return await RunAsync("Browser sign-in", CompanionSignInState.WaitingForBrowser, token => TryBrowserSignInAsync(session, token), cancellationToken);
   }



   /// <summary>Cancels the browser sign-in that is running, if one is. It has no effect once the exchange with the browser has ended, however it ended.</summary>
   public void CancelBrowserSignIn() => _browserCancel?.Cancel();



   /// <summary>
   /// The exchange under its limit, and what its ending means. The Cancel button reaches the exchange through a
   /// field, which is cleared before the exchange's cancellation sources are released, so a Cancel that arrives while
   /// a failed exchange is still being reported finds nothing to cancel. An exchange that the operator cancelled or
   /// that ran out of time is said to be exactly that, and loses its session when the callback had already been
   /// handed to it. A wait that the caller or disposal ended is left to the runner, which does the same as it
   /// releases the sign-in. Announcing the result is outside the protected part, as it is for a password sign-in.
   /// </summary>
   private async Task<QqqSessionCredential?> TryBrowserSignInAsync(ISignInSession session, CancellationToken token)
   {
      QqqCertificateRejection? known = session.LastCertificateRejection;
      using CancellationTokenSource limit = new(BrowserSignInLimit, _time);
      using CancellationTokenSource cancel = new();
      using var exchange = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancel.Token, token);
      QqqSessionCredential credential;
      _handedOver = false;
      _browserCancel = cancel;
      try
      {
         await ExchangeWithBrowserAsync(session, exchange.Token);
         exchange.Token.ThrowIfCancellationRequested();
         credential = session.ExportCredential();
      }
      catch(OperationCanceledException) when(!token.IsCancellationRequested && (cancel.IsCancellationRequested || limit.IsCancellationRequested))
      {
         _log.Info(cancel.IsCancellationRequested ? "Browser sign-in to {Server} was cancelled by the operator." : "Browser sign-in to {Server} was given up after the three minute limit.", QqqOrigin.ShownAuthority(session.Origin));
         ReportCallbackTrace();
         DropHandedOverSession();
         Settle(cancel.IsCancellationRequested ? CompanionSignInState.BrowserCanceled : CompanionSignInState.BrowserTimedOut);
         return null;
      }
      catch(Exception exception) when(Reportable(exception, token) && exception is IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException or Win32Exception or InvalidDataException or OperationCanceledException)
      {
         ReportCallbackTrace();
         Fail("Browser sign-in not completed", CompanionSignInState.BrowserFailed, session, known, exception);
         return null;
      }
      finally
      {
         _browserCancel = null;
      }
      return SignedIn(session, credential);
   }



   /// <summary>
   /// The browser exchange itself: begin the sign-in on the session, read the state it sent from the address it
   /// returned, register that state with the dispatcher (which has an inbox open before the browser is, so a fast
   /// redirect is not lost), open the provider's page in the default browser, wait for the callback with that state,
   /// and complete the sign-in on the session that started it. The registration ends when the exchange does. Until
   /// the page is open, the exchange notes what it is at, so an attempt that ends before any browser was involved can
   /// say that, and why (see <see cref="ReportCallbackTrace"/>). Only the provider's host is logged, never the
   /// address, its state or the callback. The token is looked at again after each wait: once the sign-in was begun,
   /// so a browser is not opened for a wait that has already ended, and once the callback is there, so the session
   /// is not asked to complete for one. That second look is needed because a callback that has already arrived is
   /// handed over whatever the token says, and a session is not bound to refuse: the check is made here and not left
   /// to it. Completing takes the same token, so a wait that ends while the session completes ends the completion
   /// too.
   /// </summary>
   private async Task ExchangeWithBrowserAsync(ISignInSession session, CancellationToken cancellationToken)
   {
      _log.Info("Browser sign-in requested for {Server}; this application's callback is {Callback}.", QqqOrigin.ShownAuthority(session.Origin), _callbacks.RedirectUri);
      _unopened = "the server does not list this application's callback";
      if(!_redirects.Contains(_callbacks.RedirectUri, StringComparer.Ordinal))
      {
         _log.Warning("The server's native grant does not list this application's callback {Callback}; it lists {Count} other address(es). Add the callback to the QQQ native session grant.", _callbacks.RedirectUri, _redirects.Count);
         throw new InvalidOperationException("No approved native browser callback is configured.");
      }
      _unopened = "the session had not prepared a usable sign-in address";
      Uri authorization = await session.BeginBrowserSignInAsync(new Uri(_callbacks.RedirectUri), cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      string state = BrowserCallbackDispatcher.ReadState(authorization) ?? throw new InvalidOperationException("The sign-in address the session prepared carries no state of its own, so its callback could not be told from another.");
      _unopened = "the attempt was not yet waiting for its callback";
      using BrowserCallbackTicket ticket = _dispatcher.Register(state);
      _log.Info("Opening the identity provider's sign-in page in the default browser ({Provider}).", QqqOrigin.ShownAuthority(authorization));
      _unopened = "the default browser was not started";
      BrowserLaunch.Open(_browser, authorization);
      _unopened = null;
      Uri callback = await ticket.Callback.WaitAsync(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      _log.Info("Browser callback received; completing the sign-in.");
      _handedOver = true;
      await session.CompleteBrowserSignInAsync(callback, cancellationToken);
   }



   /// <summary>
   /// Discards the session when the browser attempt that is ending had handed its callback to it. The server may
   /// have completed that sign-in, so a session kept here could be signed in behind an attempt that was reported as
   /// cancelled, timed out or stopped. An attempt that ended before the hand-over gave the session nothing, and
   /// keeps it.
   /// </summary>
   private void DropHandedOverSession()
   {
      if(!_handedOver)
      {
         return;
      }
      _handedOver = false;
      _log.Info("The browser sign-in ended without a result after its callback had been handed to the session, so the session is discarded: the server may have signed it in, and nothing here waits for that. The next attempt reads the sign-in description again.");
      DropSession();
   }



   /// <summary>
   /// Says what is known about the hand-off from the browser when an attempt ends without a result. When the
   /// provider's page was opened, it copies the protocol handler's recent trace into the activity log, or states
   /// that the browser never launched the handler at all, so a hand-off that stops outside the companion is still
   /// visible in one place. The handler writes that trace without the query of any callback. When the attempt ended
   /// before a page was opened, none of that applies: no browser was started, so nothing could have redirected or
   /// been blocked, and a trace from the last ten minutes would be some other attempt's. The entry then says that
   /// no page was opened and what the attempt had not got past: the server not listing this application's callback,
   /// the session not having prepared an address, the wait for the callback not having started, or the browser not
   /// having started.
   /// </summary>
   private void ReportCallbackTrace()
   {
      if(_unopened is { } reason)
      {
         _log.Info("This attempt ended before a browser page was opened ({Reason}), so the sign-in protocol handler was not involved and its trace is not copied here.", reason);
         return;
      }
      IReadOnlyList<string> lines = _callbacks.RecentTrace(s_traceWindow);
      if(lines.Count == 0)
      {
         _log.Warning("The browser never launched the sign-in protocol handler in the last 10 minutes (no entries in {TracePath}). Either the identity provider did not redirect to {Callback} (check the provider's allowed redirect URIs and any error page it showed), or the browser blocked opening the QQQ Print Agent sign-in link.", _callbacks.TracePath, _callbacks.RedirectUri);
         return;
      }
      _log.Warning("Protocol handler trace from {TracePath}:{NewLine}{Trace}", _callbacks.TracePath, Environment.NewLine, string.Join(Environment.NewLine, lines));
   }
}
