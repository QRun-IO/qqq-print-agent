/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.Service;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Browser sign-in part of the flow: the exchange with the identity provider through the default browser, its time limit
/// and cancellation, and what is recorded when the browser never comes back.
/// </summary>
public sealed partial class SignInFlow
{
   /// <summary>How long a browser sign-in waits for its callback before it is abandoned.</summary>
   public static readonly TimeSpan BrowserSignInLimit = TimeSpan.FromMinutes(3);

   private static readonly TimeSpan s_traceWindow = TimeSpan.FromMinutes(10);
   private CancellationTokenSource? _browserWait;
   private bool _callbackArrived;



   /// <summary>
   /// Runs the browser sign-in with a three minute limit, then hands the session to the service. The redirect address must
   /// be the one this application's installer registered, and only an address the server also lists is used, so a server
   /// cannot steer the callback elsewhere. Cancelling or timing out discards the half-finished session, and discovery has
   /// to be run again; any other failure asks the operator to try again explicitly. The limit and the Cancel button apply
   /// to the exchange with the browser, not to the hand-over that follows it, which only the caller's token or disposing
   /// the flow ends. It returns false when nothing was attempted (no session, or another operation is running).
   /// </summary>
   public async Task<bool> SignInWithBrowserAsync(PairingChoice pairing, CancellationToken cancellationToken = default)
   {
      if(_session is not { } session || !TryBegin("Browser sign-in"))
      {
         return false;
      }
      return await RunAsync("Browser sign-in", SignInStage.WaitingForBrowser, async token =>
      {
         await SignInWithBrowserCoreAsync(session, pairing, token);
         return true;
      }, stopped: true, Settled, cancellationToken);
   }



   /// <summary>
   /// The browser sign-in and its hand-over, with the Cancel button shown for as long as both take. The hand-over and what
   /// it announces are outside the part whose failures mean the sign-in failed: once the browser exchange has produced a
   /// session, a failure while the result is announced (a view's handler that throws) is not a failed sign-in.
   /// </summary>
   private async Task SignInWithBrowserCoreAsync(ISignInSession session, PairingChoice pairing, CancellationToken token)
   {
      _callbackArrived = false;
      try
      {
         BrowserPending = true;
         if(await TryBrowserExchangeAsync(session, token) is { } credential)
         {
            AfterBrowserHandOver(await HandOverAsync(credential, pairing, token));
         }
      }
      finally
      {
         BrowserPending = false;
      }
   }



   /// <summary>
   /// The browser's part of the sign-in. It returns the session credential to hand over, or null when the exchange was
   /// cancelled, timed out or failed and that was reported. "Canceled" is only said here, where nothing has been handed
   /// to the service yet; once the request to the service has left, an unanswered hand-over is reported by the hand-over
   /// itself as unconfirmed. No credential is taken once the wait has ended.
   /// </summary>
   private async Task<QqqSessionCredential?> TryBrowserExchangeAsync(ISignInSession session, CancellationToken token)
   {
      try
      {
         _log.Info("Browser sign-in requested for " + session.Origin.Authority + "; this application's callback is " + _callbacks.RedirectUri + ".");
         string redirect = _metadata?.Values?.NativeSessionLogin?.RedirectUris.FirstOrDefault(value => string.Equals(value, _callbacks.RedirectUri, StringComparison.Ordinal))
            ?? throw Unlisted();
         await WaitForBrowserAsync(session, new Uri(redirect), token);
         token.ThrowIfCancellationRequested();
         return session.ExportCredential();
      }
      catch(OperationCanceledException) when(!_disposed)
      {
         AbandonBrowserSignIn(_callbackArrived
            ? "Browser sign-in cancelled or timed out (three minute limit) after the callback arrived, before the sign-in was completed."
            : "Browser sign-in cancelled or timed out (three minute limit) before a callback arrived.");
         return null;
      }
      catch(Exception exception) when(!_disposed && exception is IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException or Win32Exception or InvalidDataException)
      {
         LogFailure("Browser sign-in not completed", exception);
         ReportCallbackTrace();
         Report(SignInMessages.BrowserNotCompleted(QqqSessionDiagnostics.Explain(exception)));
         return null;
      }
   }



   /// <summary>Cancels the browser sign-in that is waiting for the browser, if one is; the Cancel button and closing the dialog both end here. It has no effect once the exchange with the browser has ended, however it ended.</summary>
   public void CancelBrowserSignIn() => _browserWait?.Cancel();



   /// <summary>
   /// Waits for the browser under the three minute limit. The Cancel button reaches this wait through a field, and the
   /// field is cleared before the wait's cancellation sources are released, so a Cancel that arrives while a failed
   /// exchange is still being reported finds nothing to cancel instead of a source that no longer exists.
   /// </summary>
   private async Task WaitForBrowserAsync(ISignInSession session, Uri redirect, CancellationToken cancellationToken)
   {
      using CancellationTokenSource limit = new(BrowserSignInLimit, _time);
      using var exchange = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);
      _browserWait = exchange;
      try
      {
         Report(SignInMessages.WaitingForBrowser);
         await ExchangeWithBrowserAsync(session, redirect, exchange.Token);
      }
      finally
      {
         _browserWait = null;
      }
   }



   /// <summary>
   /// The browser exchange itself: open the callback inbox first (so a fast redirect is not lost), begin the login, open the
   /// provider's page in the default browser, wait for the forwarded callback, and complete the login on the session
   /// that started it. The inbox is disposed when the exchange ends. Only the provider's host is logged. A callback that is
   /// malformed or too large is refused by the inbox as invalid data, which the caller reports like any other failed exchange.
   /// The token is looked at again after each wait, so a browser is not opened and a login is not completed for a wait
   /// that has already ended.
   /// </summary>
   private async Task ExchangeWithBrowserAsync(ISignInSession session, Uri redirect, CancellationToken cancellationToken)
   {
      using IBrowserCallbackInbox inbox = _callbacks.Open();
      _log.Info("Browser callback inbox open; starting the sign-in attempt.");
      Uri authorization = await session.BeginBrowserSignInAsync(redirect, cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      _log.Info("Opening the identity provider's sign-in page in the default browser (" + QqqOrigin.ShownAuthority(authorization) + ").");
      _browser.Open(authorization);
      Uri callback = await inbox.ReceiveAsync(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      _callbackArrived = true;
      _log.Info("Browser callback received; completing the sign-in.");
      await session.CompleteBrowserSignInAsync(callback, cancellationToken);
   }



   /// <summary>
   /// Handles a hand-over that never left this computer; every other outcome was already reported by the hand-over. When
   /// the service did not accept a connection in time, nothing was handed over, so the attempt ends like a cancelled
   /// browser sign-in, and the log says what really happened. Any other failure before the request left is "not
   /// completed" with the reason.
   /// </summary>
   private void AfterBrowserHandOver(ServiceResult<SignInConfirmation> handover)
   {
      if(handover is not { Outcome: ServiceOutcome.Unavailable, Unconfirmed: false })
      {
         return;
      }
      if(handover.Failure == ServiceFailure.TimedOut)
      {
         AbandonBrowserSignIn("The service did not accept a connection for the hand-over after the browser sign-in completed; nothing was handed over, and the attempt was discarded.");
         return;
      }
      _log.Error("Browser sign-in not completed: the service did not answer (" + Describe(handover) + ").");
      ReportCallbackTrace();
      Report(SignInMessages.BrowserNotCompleted(handover.Message));
   }



   /// <summary>Ends a cancelled or timed-out browser sign-in with the log entry that says where it stopped: the session and the methods it offered are discarded, and the operator is told to run discovery again. It is only used while nothing has been sent to the service.</summary>
   private void AbandonBrowserSignIn(string entry)
   {
      _log.Info(entry);
      ReportCallbackTrace();
      DropSession();
      Report(SignInMessages.BrowserCanceled);
   }



   /// <summary>
   /// Copies the protocol handler's recent trace into the activity log, or states that the browser never launched the
   /// handler at all, so a hand-off that stops outside the companion is still visible in one place.
   /// </summary>
   private void ReportCallbackTrace()
   {
      IReadOnlyList<string> lines = _callbacks.RecentTrace(s_traceWindow);
      if(lines.Count == 0)
      {
         _log.Warning("The browser never launched the sign-in protocol handler in the last 10 minutes (no entries in " + _callbacks.TracePath
            + "). Either the identity provider did not redirect to " + _callbacks.RedirectUri + " (check the provider's allowed redirect URIs and any error page it showed), or the browser blocked opening the QQQ Print Agent sign-in link.");
         return;
      }
      _log.Warning("Protocol handler trace from " + _callbacks.TracePath + ":" + Environment.NewLine + string.Join(Environment.NewLine, lines));
   }



   /// <summary>Logs which callbacks the server does list before reporting that this application's callback is not among them.</summary>
   private InvalidOperationException Unlisted()
   {
      _log.Warning("The server's native grant does not list this application's callback " + _callbacks.RedirectUri + "; it lists ["
         + string.Join(", ", _metadata?.Values?.NativeSessionLogin?.RedirectUris ?? []) + "]. Add the callback to the QQQ native session grant.");
      return new InvalidOperationException("No approved native browser callback is configured.");
   }
}
