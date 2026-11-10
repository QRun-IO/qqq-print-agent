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



   /// <summary>
   /// Runs the browser sign-in with a three minute limit, then hands the session to the service. The redirect address must
   /// be the one this application's installer registered, and only an address the server also lists is used, so a server
   /// cannot steer the callback elsewhere. Cancelling or timing out discards the half-finished session and withdraws the
   /// browser option until discovery is run again; any other failure asks the operator to try again explicitly.
   /// </summary>
   public async Task SignInWithBrowserAsync(PairingChoice pairing)
   {
      if(IsRunning || _session is not { } session)
      {
         return;
      }
      bool confirmed = false;
      BrowserPending = true;
      Stage = SignInStage.WaitingForBrowser;
      try
      {
         _log.Info("Browser sign-in requested for " + session.Origin.Authority + "; this application's callback is " + _callbacks.RedirectUri + ".");
         string redirect = _metadata?.Values?.NativeSessionLogin?.RedirectUris.FirstOrDefault(value => string.Equals(value, _callbacks.RedirectUri, StringComparison.Ordinal))
            ?? throw Unlisted();
         using CancellationTokenSource limit = new(BrowserSignInLimit, _time);
         _browserWait = limit;
         Report(SignInMessages.WaitingForBrowser);
         await ExchangeWithBrowserAsync(session, new Uri(redirect), limit.Token);
         ServiceResult<string> handover = await HandOverAsync(session, pairing);
         confirmed = handover.Succeeded;
         AfterBrowserHandOver(handover);
      }
      catch(OperationCanceledException)
      {
         AbandonBrowserSignIn();
      }
      catch(Exception exception) when(exception is IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException or Win32Exception)
      {
         LogFailure("Browser sign-in not completed", exception);
         ReportCallbackTrace();
         Report(SignInMessages.BrowserNotCompleted(QqqSessionDiagnostics.Explain(exception)));
      }
      finally
      {
         _browserWait = null;
         BrowserPending = false;
         Settle(confirmed);
      }
   }



   /// <summary>Cancels the browser sign-in that is waiting for its callback, if one is running; the Cancel button and closing the dialog both end here.</summary>
   public void CancelBrowserSignIn() => _browserWait?.Cancel();



   /// <summary>
   /// The browser exchange itself: open the callback inbox first (so a fast redirect is not lost), begin the login, open the
   /// provider's page in the default browser, wait for the forwarded callback, and complete the login on the session
   /// that started it. The inbox is disposed when the exchange ends. Only the provider's host is logged.
   /// </summary>
   private async Task ExchangeWithBrowserAsync(ISignInSession session, Uri redirect, CancellationToken cancellationToken)
   {
      using IBrowserCallbackInbox inbox = _callbacks.Open();
      _log.Info("Browser callback inbox open; starting the sign-in attempt.");
      Uri authorization = await session.BeginBrowserSignInAsync(redirect, cancellationToken);
      _log.Info("Opening the identity provider's sign-in page in the default browser (" + authorization.Authority + ").");
      _browser.Open(authorization);
      Uri callback = await inbox.ReceiveAsync(cancellationToken);
      _log.Info("Browser callback received; completing the sign-in.");
      await session.CompleteBrowserSignInAsync(callback, cancellationToken);
   }



   /// <summary>
   /// Handles a hand-over the service did not answer. A time limit or cancellation is treated like a cancelled browser
   /// sign-in, and any other failure as "not completed" with the reason, which is how these failures have always been
   /// shown for a browser sign-in. When the service answered, the hand-over already reported the result.
   /// </summary>
   private void AfterBrowserHandOver(ServiceResult<string> handover)
   {
      if(handover.Outcome != ServiceOutcome.Unavailable)
      {
         return;
      }
      if(handover.Interrupted)
      {
         AbandonBrowserSignIn();
         return;
      }
      _log.Error("Browser sign-in not completed: the service did not answer (" + handover.Failure + ").");
      ReportCallbackTrace();
      Report(SignInMessages.BrowserNotCompleted(handover.Message));
   }



   /// <summary>Ends a cancelled or timed-out browser sign-in: the session is discarded, the browser option is withdrawn, and the operator is told to run discovery again.</summary>
   private void AbandonBrowserSignIn()
   {
      _log.Info("Browser sign-in cancelled or timed out (three minute limit) before a callback arrived.");
      ReportCallbackTrace();
      DropSession();
      BrowserOffered = false;
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
