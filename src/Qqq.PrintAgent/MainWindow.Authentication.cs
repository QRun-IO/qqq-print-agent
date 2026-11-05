/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>
/// Sign-in part of the main window. The companion discovers what the server offers, signs the operator in (password or
/// browser), and hands only the resulting session credential to the service, which keeps it protected; the window never
/// stores a password or a provider token, and the password box is cleared after every attempt.
/// </summary>
public sealed partial class MainWindow
{
   private QqqSessionClient? _signInClient;
   private QqqAuthenticationMetadata? _signInMetadata;
   private CancellationTokenSource? _browserSignInCancellation;
   private ServerTrust _serverTrust = new();



   /// <summary>The companion's progress sink for the sign-in client: steps at INFO, request detail at DEBUG, in the shared log the service also writes.</summary>
   private static QqqSessionDiagnostics CompanionDiagnostics() => new(message => App.Log.Info("[Companion] " + message),
      message => App.Log.Debug("[Companion] " + message), () => App.Log.IsDebugEnabled);



   /// <summary>Records a companion-side failure: type and message at ERROR, the full exception as debug detail.</summary>
   private static void LogFailure(string context, Exception exception)
   {
      App.Log.Error("[Companion] " + context + ": " + QqqSessionDiagnostics.Describe(exception));
      App.Log.Debug("[Companion] " + context + " detail: " + exception);
   }



   /// <summary>Reads the service's transport trust setting into the connection dialog's checkbox; when the service cannot be reached the box stays unchecked and the reason is logged.</summary>
   private async Task LoadServerTrustAsync()
   {
      try
      {
         string response = await _sendRequest(new PipeRequest("get-server-trust"));
         if(!response.StartsWith("Error:", StringComparison.Ordinal))
         {
            _serverTrust = JsonSerializer.Deserialize<ServerTrust>(response, Qqq.PrintAgent.Core.Printing.PrintApiClient.JsonOptions) ?? new();
            ShowServerTrust();
            return;
         }
         App.Log.Warning("[Companion] Could not read the server trust setting: " + response);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException)
      {
         LogFailure("Could not read the server trust setting from the service", exception);
      }
      _serverTrust = new ServerTrust();
      ShowServerTrust();
   }



   /// <summary>Reflects the saved exception in the checkbox and names the server it is bound to, so an exception saved for another server is visibly not in force for the one being entered.</summary>
   private void ShowServerTrust()
   {
      AllowUntrustedCertificateBox.IsChecked = _serverTrust.AllowUntrustedCertificate;
      AllowUntrustedCertificateBox.Content = _serverTrust.AllowUntrustedCertificate
         ? "Allow untrusted server certificate for " + _serverTrust.Origin + " (development only)"
         : "Allow untrusted server certificate for this server (development only)";
   }



   /// <summary>True when the saved exception is on and bound to exactly the server being signed in to.</summary>
   private bool AllowsUntrustedCertificate(Uri origin) => _serverTrust.AppliesTo(origin);



   /// <summary>Saves the checkbox to the service so the service's own sessions honor it, and logs the change loudly; a failed save reverts the box and shows why.</summary>
   private async void AllowUntrustedCertificate_Click(object sender, RoutedEventArgs e)
   {
      bool allow = AllowUntrustedCertificateBox.IsChecked == true;
      Uri? origin = null;
      if(allow && !Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out origin))
      {
         AllowUntrustedCertificateBox.IsChecked = false;
         Feedback("Enter the server address first", "The certificate exception is saved for one server only, so the server address must be entered before it can be allowed.", InfoBarSeverity.Warning);
         return;
      }
      ServerTrust requested = allow ? new ServerTrust(true, ServerTrust.Normalize(origin!)) : new ServerTrust();
      try
      {
         string response = await _sendRequest(new PipeRequest("set-server-trust", ServerTrust: requested));
         if(response.StartsWith("Error:", StringComparison.Ordinal))
         {
            ShowServerTrust();
            Feedback("Server trust not saved", response, InfoBarSeverity.Error);
            return;
         }
         _serverTrust = requested;
         ShowServerTrust();
         if(allow)
         {
            App.Log.Warning("[Companion] Untrusted server certificates ALLOWED by the operator for " + requested.Origin + " (development only).");
            Feedback("Untrusted certificate allowed for " + requested.Origin, "The service and companion will accept any certificate from this one server until this is turned off. Other servers keep normal validation. Use only with a development server.", InfoBarSeverity.Warning);
         }
         else
         {
            App.Log.Info("[Companion] Untrusted server certificates refused again for every server (default).");
            Feedback("Untrusted certificates refused", "Only certificates Windows trusts for the service account are accepted again.", InfoBarSeverity.Informational);
         }
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException)
      {
         ShowServerTrust();
         LogFailure("Server trust setting could not be saved", exception);
         ServiceUnavailable();
      }
   }



   /// <summary>Cancels the browser sign-in that is waiting for its callback, if one is running.</summary>
   private void CancelBrowserSignIn_Click(object sender, RoutedEventArgs e) => _browserSignInCancellation?.Cancel();



   /// <summary>Cancels a waiting browser sign-in when the connection dialog closes, so no attempt keeps listening after the operator left.</summary>
   private void ConnectionDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args) => _browserSignInCancellation?.Cancel();



   /// <summary>
   /// Asks the entered server which sign-in methods it offers and shows only those: the password form for a table-based
   /// server, and the browser button when the server advertises a native grant. A server that offers neither is reported as
   /// needing configuration. Any previous sign-in client is disposed first, so each attempt starts from a clean session.
   /// </summary>
   private async void DiscoverSignIn_Click(object sender, RoutedEventArgs e)
   {
      if(_busy)
      { return; }
      SetBusy(true);
      PasswordSignInPanel.Visibility = Visibility.Collapsed;
      BrowserSignInButton.Visibility = Visibility.Collapsed;
      try
      {
         _signInClient?.Dispose();
         _signInClient = null;
         string entered = ServerBox.Text.Trim();
         App.Log.Info("[Companion] Discover sign-in options requested for '" + entered + "'.");
         if(!Uri.TryCreate(entered, UriKind.Absolute, out Uri? origin))
         {
            App.Log.Warning("[Companion] The server address is not an absolute URL (expected https://host[:port]).");
            throw new InvalidOperationException("Enter a valid server origin.");
         }
         QqqSessionClient client = new(origin, allowLoopbackHttp: origin.IsLoopback, diagnostics: CompanionDiagnostics(), allowUntrustedCertificate: AllowsUntrustedCertificate(origin));
         _signInClient = client;
         _ = await client.DiscoverAsync();
         _signInMetadata = await client.GetAuthenticationMetadataAsync();
         PasswordSignInPanel.Visibility = string.Equals(_signInMetadata.Type, "TABLE_BASED", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
         BrowserSignInButton.Visibility = _signInMetadata.Values?.NativeSessionLogin is not null ? Visibility.Visible : Visibility.Collapsed;
         bool supported = PasswordSignInPanel.Visibility == Visibility.Visible || BrowserSignInButton.Visibility == Visibility.Visible;
         App.Log.Info("[Companion] Sign-in options for " + origin.Authority + ": password sign-in "
            + (PasswordSignInPanel.Visibility == Visibility.Visible ? "offered" : "not offered") + ", browser sign-in "
            + (BrowserSignInButton.Visibility == Visibility.Visible ? "offered" : "not offered") + ".");
         Feedback(supported ? "Sign-in options ready" : "Server configuration required",
            supported ? "Use the sign-in method configured by this server." : "This server has not enabled a supported native sign-in grant. Ask its administrator to configure it.",
            supported ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
      }
      catch(Exception exception) when(exception is IOException or ArgumentException or InvalidOperationException or HttpRequestException or OperationCanceledException)
      {
         LogFailure("Discovery failed", exception);
         Feedback("Discovery failed", "The server did not provide compatible authentication and printing discovery. Reason: " + QqqSessionDiagnostics.Explain(exception) + " The activity log has the full detail.", InfoBarSeverity.Error);
      }
      finally { SetBusy(false); }
   }



   /// <summary>
   /// Signs in with the entered user name and password, then transfers the resulting session to the service. The password
   /// box is cleared in a finally block whether or not sign-in worked, and the failure message deliberately does not say
   /// which part (credentials, device ownership or permission) was wrong.
   /// </summary>
   private async void PasswordSignIn_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || _signInClient is null)
      { return; }
      SetBusy(true);
      try
      {
         App.Log.Info("[Companion] Password sign-in submitted for " + _signInClient.Origin.Authority + ".");
         await _signInClient.SignInWithPasswordAsync(UsernameBox.Text.Trim(), QqqPasswordBox.Password);
         await TransferSignInAsync();
      }
      catch(Exception exception) when(exception is IOException or ArgumentException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
      {
         LogFailure("Password sign-in not completed", exception);
         Feedback("Sign-in not completed", "The server did not authorize this connection, or the service could not confirm it. Reason: " + QqqSessionDiagnostics.Explain(exception) + " The activity log has the full detail.", InfoBarSeverity.Error);
      }
      finally
      {
         QqqPasswordBox.Password = string.Empty;
         SetBusy(false);
      }
   }



   /// <summary>
   /// Hands the session credential, and nothing else, to the service together with the operator's pairing choice (keep the
   /// current pairing, claim a legacy device, or start a new pairing). The service verifies it and keeps it protected; the
   /// result is shown as success only when the service confirms it signed in.
   /// </summary>
   private async Task TransferSignInAsync()
   {
      string choice = PairingChoice.SelectedIndex == 2 ? "start a new pairing" : PairingChoice.SelectedIndex == 1 ? "associate with the existing pairing" : "keep the current pairing";
      App.Log.Info("[Companion] Sign-in accepted by the server; handing the session to the service (" + choice + ").");
      string result = await _sendRequest(new PipeRequest("sign-in", SessionCredential: _signInClient!.ExportCredential(),
         ClaimLegacyDevice: PairingChoice.SelectedIndex == 1, StartNewPairing: PairingChoice.SelectedIndex == 2));
      bool success = result.StartsWith("Signed in to " + AppIdentity.ServerName + ".", StringComparison.Ordinal);
      if(success)
      { App.Log.Info("[Companion] Service sign-in result: " + result); }
      else
      { App.Log.Error("[Companion] Service sign-in result: " + result); }
      Feedback(success ? "Signed in" : "Service authorization not completed", result, success ? InfoBarSeverity.Success : InfoBarSeverity.Error);
   }



   /// <summary>Asks the service to sign out and forget its session, then drops the window's own sign-in client. Pairing and job history are not removed.</summary>
   private async void SignOut_Click(object sender, RoutedEventArgs e)
   {
      if(_busy)
      { return; }
      SetBusy(true);
      try
      {
         App.Log.Info("[Companion] Sign-out requested; asking the service to end the session.");
         string result = await _sendRequest(new PipeRequest("sign-out"));
         _signInClient?.Dispose();
         _signInClient = null;
         App.Log.Info("[Companion] Service sign-out result: " + result);
         Feedback("Account sign-out", result, result.StartsWith("Error:", StringComparison.Ordinal) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException)
      {
         LogFailure("Sign-out could not reach the service", exception);
         ServiceUnavailable();
      }
      finally { SetBusy(false); }
   }



   /// <summary>
   /// Runs the browser sign-in with a three minute limit and a Cancel button. The redirect address must be the one this
   /// application's installer registered, and only an address the server also lists is used, so a server cannot steer the
   /// callback elsewhere. Cancelling discards the half-finished client, and a failure asks the operator to start again explicitly.
   /// </summary>
   private async void BrowserSignIn_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || _signInClient is null)
      { return; }
      SetBusy(true);
      CancelBrowserSignInButton.Visibility = Visibility.Visible;
      try
      {
         App.Log.Info("[Companion] Browser sign-in requested for " + _signInClient.Origin.Authority + "; this application's callback is " + QqqBrowserCallback.RedirectUri + ".");
         string redirect = _signInMetadata?.Values?.NativeSessionLogin?.RedirectUris.FirstOrDefault(value =>
            string.Equals(value, QqqBrowserCallback.RedirectUri, StringComparison.Ordinal))
            ?? throw Unlisted();
         using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
         _browserSignInCancellation = timeout;
         await CompleteBrowserSignInAsync(new Uri(redirect), timeout.Token);
         await TransferSignInAsync();
      }
      catch(OperationCanceledException)
      {
         App.Log.Info("[Companion] Browser sign-in cancelled or timed out (three minute limit) before a callback arrived.");
         ReportCallbackTrace();
         _signInClient?.Dispose();
         _signInClient = null;
         BrowserSignInButton.Visibility = Visibility.Collapsed;
         Feedback("Browser sign-in canceled", "No new sign-in was accepted. Choose Discover sign-in options to start a new attempt.", InfoBarSeverity.Informational);
      }
      catch(Exception exception) when(exception is IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
      {
         LogFailure("Browser sign-in not completed", exception);
         ReportCallbackTrace();
         Feedback("Browser sign-in not completed", "No validated account session was received. Reason: " + QqqSessionDiagnostics.Explain(exception) + " Check the server's advertised native sign-in grant and installed callback configuration, then try again explicitly. The activity log has the full detail.", InfoBarSeverity.Warning);
      }
      finally
      {
         _browserSignInCancellation = null;
         CancelBrowserSignInButton.Visibility = Visibility.Collapsed;
         SetBusy(false);
      }
   }



   /// <summary>
   /// Copies the protocol handler's recent trace into the activity log, or states that the browser never launched the
   /// handler at all, so a hand-off that stops outside the companion is still visible in one place.
   /// </summary>
   private static void ReportCallbackTrace()
   {
      IReadOnlyList<string> lines = QqqBrowserCallback.RecentTrace(TimeSpan.FromMinutes(10));
      if(lines.Count == 0)
      {
         App.Log.Warning("[Companion] The browser never launched the sign-in protocol handler in the last 10 minutes (no entries in " + QqqBrowserCallback.TracePath
            + "). Either the identity provider did not redirect to " + QqqBrowserCallback.RedirectUri + " (check the provider's allowed redirect URIs and any error page it showed), or the browser blocked opening the QQQ Print Agent sign-in link.");
         return;
      }
      App.Log.Warning("[Companion] Protocol handler trace from " + QqqBrowserCallback.TracePath + ":" + Environment.NewLine + string.Join(Environment.NewLine, lines));
   }



   /// <summary>
   /// The browser exchange itself: open the callback inbox first (so a fast redirect is not lost), begin the login, open the
   /// provider's page in the default browser, wait for the forwarded callback, and complete the login against the attempt
   /// that started it. The inbox is disposed when the exchange ends.
   /// </summary>
   private async Task CompleteBrowserSignInAsync(Uri redirect, CancellationToken token)
   {
      using QqqBrowserCallback inbox = new();
      App.Log.Info("[Companion] Browser callback inbox open; starting the sign-in attempt.");
      QqqBrowserLogin attempt = await _signInClient!.BeginBrowserLoginAsync(redirect, token);
      App.Log.Info("[Companion] Opening the identity provider's sign-in page in the default browser (" + attempt.AuthorizationUri.Authority + ").");
      _ = Process.Start(new ProcessStartInfo(attempt.AuthorizationUri.AbsoluteUri) { UseShellExecute = true });
      Uri callbackUri = await inbox.ReceiveAsync(token);
      App.Log.Info("[Companion] Browser callback received; completing the sign-in.");
      await _signInClient.CompleteBrowserLoginAsync(attempt, callbackUri, token);
   }



   /// <summary>Logs which callbacks the server does list before reporting that this application's callback is not among them.</summary>
   private InvalidOperationException Unlisted()
   {
      App.Log.Warning("[Companion] The server's native grant does not list this application's callback " + QqqBrowserCallback.RedirectUri + "; it lists ["
         + string.Join(", ", _signInMetadata?.Values?.NativeSessionLogin?.RedirectUris ?? []) + "]. Add the callback to the QQQ native session grant.");
      return new InvalidOperationException("No approved native browser callback is configured.");
   }
}
