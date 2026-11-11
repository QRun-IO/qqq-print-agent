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
/// </summary>
public sealed partial class CompanionSignIn
{
   /// <summary>How long a browser sign-in waits before it is given up.</summary>
   public static readonly TimeSpan BrowserSignInLimit = TimeSpan.FromMinutes(3);

   private static readonly TimeSpan s_traceWindow = TimeSpan.FromMinutes(10);
   private CancellationTokenSource? _browserCancel;



   /// <summary>
   /// Signs in through the browser, with a three minute limit, and returns the session credential. Nothing happens
   /// without a session that offers the browser. The redirect address is the one this application's installer
   /// registered, and it is used only when the server also lists it, so a server cannot steer the callback elsewhere.
   /// A cancelled, timed-out or failed attempt is reported by state and keeps the session, so the operator can try
   /// again: a late callback of the abandoned attempt carries a state nobody waits for and is dropped by the
   /// dispatcher. It returns null when no credential was obtained.
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
   /// that ran out of time is said to be exactly that. A wait that the caller or disposal ended is left to the
   /// runner. Announcing the result is outside the protected part, as it is for a password sign-in.
   /// </summary>
   private async Task<QqqSessionCredential?> TryBrowserSignInAsync(ISignInSession session, CancellationToken token)
   {
      QqqCertificateRejection? known = session.LastCertificateRejection;
      using CancellationTokenSource limit = new(BrowserSignInLimit, _time);
      using CancellationTokenSource cancel = new();
      using var exchange = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancel.Token, token);
      QqqSessionCredential credential;
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
   /// and complete the sign-in on the session that started it. The registration ends when the exchange does. Only the
   /// provider's host is logged, never the address, its state or the callback. The token is looked at again after
   /// each wait: once the sign-in was begun, so a browser is not opened for a wait that has already ended, and once
   /// the callback is there, so the session is not asked to complete for one. That second look is needed because a
   /// callback that has already arrived is handed over whatever the token says, and a session is not bound to
   /// refuse: the check is made here and not left to it. Completing takes the same token, so a wait that ends while
   /// the session completes ends the completion too.
   /// </summary>
   private async Task ExchangeWithBrowserAsync(ISignInSession session, CancellationToken cancellationToken)
   {
      _log.Info("Browser sign-in requested for {Server}; this application's callback is {Callback}.", QqqOrigin.ShownAuthority(session.Origin), _callbacks.RedirectUri);
      if(!_redirects.Contains(_callbacks.RedirectUri, StringComparer.Ordinal))
      {
         _log.Warning("The server's native grant does not list this application's callback {Callback}; it lists {Count} other address(es). Add the callback to the QQQ native session grant.", _callbacks.RedirectUri, _redirects.Count);
         throw new InvalidOperationException("No approved native browser callback is configured.");
      }
      Uri authorization = await session.BeginBrowserSignInAsync(new Uri(_callbacks.RedirectUri), cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      string state = BrowserCallbackDispatcher.ReadState(authorization) ?? throw new InvalidOperationException("The sign-in address the session prepared carries no state of its own, so its callback could not be told from another.");
      using BrowserCallbackTicket ticket = _dispatcher.Register(state);
      _log.Info("Opening the identity provider's sign-in page in the default browser ({Provider}).", QqqOrigin.ShownAuthority(authorization));
      BrowserLaunch.Open(_browser, authorization);
      Uri callback = await ticket.Callback.WaitAsync(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      _log.Info("Browser callback received; completing the sign-in.");
      await session.CompleteBrowserSignInAsync(callback, cancellationToken);
   }



   /// <summary>
   /// Copies the protocol handler's recent trace into the activity log, or states that the browser never launched the
   /// handler at all, so a hand-off that stops outside the companion is still visible in one place. The handler
   /// writes that trace without the query of any callback.
   /// </summary>
   private void ReportCallbackTrace()
   {
      IReadOnlyList<string> lines = _callbacks.RecentTrace(s_traceWindow);
      if(lines.Count == 0)
      {
         _log.Warning("The browser never launched the sign-in protocol handler in the last 10 minutes (no entries in {TracePath}). Either the identity provider did not redirect to {Callback} (check the provider's allowed redirect URIs and any error page it showed), or the browser blocked opening the QQQ Print Agent sign-in link.", _callbacks.TracePath, _callbacks.RedirectUri);
         return;
      }
      _log.Warning("Protocol handler trace from {TracePath}:{NewLine}{Trace}", _callbacks.TracePath, Environment.NewLine, string.Join(Environment.NewLine, lines));
   }
}
