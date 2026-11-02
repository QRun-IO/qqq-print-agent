/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
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
         if(!Uri.TryCreate(ServerBox.Text.Trim(), UriKind.Absolute, out Uri? origin))
         {
            throw new InvalidOperationException("Enter a valid server origin.");
         }
         QqqSessionClient client = new(origin, allowLoopbackHttp: origin.IsLoopback);
         _signInClient = client;
         _ = await client.DiscoverAsync();
         _signInMetadata = await client.GetAuthenticationMetadataAsync();
         PasswordSignInPanel.Visibility = string.Equals(_signInMetadata.Type, "TABLE_BASED", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
         BrowserSignInButton.Visibility = _signInMetadata.Values?.NativeSessionLogin is not null ? Visibility.Visible : Visibility.Collapsed;
         bool supported = PasswordSignInPanel.Visibility == Visibility.Visible || BrowserSignInButton.Visibility == Visibility.Visible;
         Feedback(supported ? "Sign-in options ready" : "Server configuration required",
            supported ? "Use the sign-in method configured by this server." : "This server has not enabled a supported native sign-in grant. Ask its administrator to configure it.",
            supported ? InfoBarSeverity.Informational : InfoBarSeverity.Warning);
      }
      catch(Exception exception) when(exception is IOException or ArgumentException or InvalidOperationException or HttpRequestException or OperationCanceledException)
      {
         Feedback("Discovery failed", "The server did not provide compatible authentication and printing discovery. Check its address and configuration.", InfoBarSeverity.Error);
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
         await _signInClient.SignInWithPasswordAsync(UsernameBox.Text.Trim(), QqqPasswordBox.Password);
         await TransferSignInAsync();
      }
      catch(Exception exception) when(exception is IOException or ArgumentException or HttpRequestException or OperationCanceledException or UnauthorizedAccessException)
      {
         Feedback("Sign-in not completed", "The server did not authorize this connection, or the service could not confirm it. Check your sign-in, device ownership and current permissions.", InfoBarSeverity.Error);
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
      string result = await _sendRequest(new PipeRequest("sign-in", SessionCredential: _signInClient!.ExportCredential(),
         ClaimLegacyDevice: PairingChoice.SelectedIndex == 1, StartNewPairing: PairingChoice.SelectedIndex == 2));
      bool success = result.StartsWith("Signed in to QQQ.", StringComparison.Ordinal);
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
         string result = await _sendRequest(new PipeRequest("sign-out"));
         _signInClient?.Dispose();
         _signInClient = null;
         Feedback("Account sign-out", result, result.StartsWith("Error:", StringComparison.Ordinal) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or OperationCanceledException)
      { ServiceUnavailable(); }
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
         string redirect = _signInMetadata?.Values?.NativeSessionLogin?.RedirectUris.FirstOrDefault(value =>
            string.Equals(value, QqqBrowserCallback.RedirectUri, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("No approved native browser callback is configured.");
         using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
         _browserSignInCancellation = timeout;
         await CompleteBrowserSignInAsync(new Uri(redirect), timeout.Token);
         await TransferSignInAsync();
      }
      catch(OperationCanceledException)
      {
         _signInClient?.Dispose();
         _signInClient = null;
         BrowserSignInButton.Visibility = Visibility.Collapsed;
         Feedback("Browser sign-in canceled", "No new sign-in was accepted. Choose Discover sign-in options to start a new attempt.", InfoBarSeverity.Informational);
      }
      catch(Exception exception) when(exception is IOException or InvalidOperationException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
      {
         Feedback("Browser sign-in not completed", "No validated account session was received. Check the server's advertised native sign-in grant and installed callback configuration, then try again explicitly.", InfoBarSeverity.Warning);
      }
      finally
      {
         _browserSignInCancellation = null;
         CancelBrowserSignInButton.Visibility = Visibility.Collapsed;
         SetBusy(false);
      }
   }



   /// <summary>
   /// The browser exchange itself: open the callback inbox first (so a fast redirect is not lost), begin the login, open the
   /// provider's page in the default browser, wait for the forwarded callback, and complete the login against the attempt
   /// that started it. The inbox is disposed when the exchange ends.
   /// </summary>
   private async Task CompleteBrowserSignInAsync(Uri redirect, CancellationToken token)
   {
      using QqqBrowserCallback inbox = new();
      QqqBrowserLogin attempt = await _signInClient!.BeginBrowserLoginAsync(redirect, token);
      _ = Process.Start(new ProcessStartInfo(attempt.AuthorizationUri.AbsoluteUri) { UseShellExecute = true });
      Uri callbackUri = await inbox.ReceiveAsync(token);
      await _signInClient.CompleteBrowserLoginAsync(attempt, callbackUri, token);
   }
}
