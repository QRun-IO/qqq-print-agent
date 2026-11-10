/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Qqq.PrintAgent.Presentation.SignIn;

namespace Qqq.PrintAgent;

/// <summary>
/// Connection dialog part of the main window. What happens on each button (discovery, password and browser sign-in, the
/// hand-over to the service, sign-out, enrollment and the certificate exception) is decided by <see cref="SignInFlow"/>;
/// this part passes the dialog's fields to it, marks the window busy around each operation, and shows the state and
/// messages the flow announces. The window never stores a password or a provider token, and the password and enrollment
/// boxes are cleared after every attempt.
/// </summary>
public sealed partial class MainWindow
{
   private readonly SignInFlow _signIn;



   /// <summary>
   /// Shows what the flow announces: which sign-in methods are offered, whether a browser sign-in can be cancelled, the
   /// saved certificate exception, and each message. The flow raises these on the thread that called it, which is the UI thread.
   /// </summary>
   private void SignIn_PropertyChanged(object? sender, PropertyChangedEventArgs e)
   {
      switch(e.PropertyName)
      {
         case nameof(SignInFlow.PasswordOffered):
            PasswordSignInPanel.Visibility = _signIn.PasswordOffered ? Visibility.Visible : Visibility.Collapsed;
            break;
         case nameof(SignInFlow.BrowserOffered):
            BrowserSignInButton.Visibility = _signIn.BrowserOffered ? Visibility.Visible : Visibility.Collapsed;
            break;
         case nameof(SignInFlow.BrowserPending):
            CancelBrowserSignInButton.Visibility = _signIn.BrowserPending ? Visibility.Visible : Visibility.Collapsed;
            break;
         case nameof(SignInFlow.Trust):
            ShowServerTrust();
            break;
         case nameof(SignInFlow.Feedback) when _signIn.Feedback is { } message:
            Feedback(message);
            break;
         default:
            break;
      }
   }



   /// <summary>Reads the service's certificate exception into the connection dialog's checkbox before the dialog opens; when the service cannot be reached the box stays unchecked and the flow logs the reason.</summary>
   private Task LoadServerTrustAsync() => _signIn.LoadTrustAsync();



   /// <summary>Reflects the saved exception in the checkbox and names the server it is bound to, so an exception saved for another server is visibly not in force for the one being entered.</summary>
   private void ShowServerTrust()
   {
      AllowUntrustedCertificateBox.IsChecked = _signIn.Trust.AllowUntrustedCertificate;
      AllowUntrustedCertificateLabel.Text = SignInMessages.TrustLabel(_signIn.Trust);
   }



   /// <summary>Saves the checkbox through the flow so the service's own sessions honor it; a save that did not happen puts the box back, and an unreachable service is shown as such.</summary>
   private async void AllowUntrustedCertificate_Click(object sender, RoutedEventArgs e)
   {
      if(!await _signIn.SetTrustAsync(AllowUntrustedCertificateBox.IsChecked == true, ServerBox.Text))
      {
         ServiceUnavailable();
      }
   }



   /// <summary>Cancels the browser sign-in that is waiting for its callback, if one is running.</summary>
   private void CancelBrowserSignIn_Click(object sender, RoutedEventArgs e) => _signIn.CancelBrowserSignIn();



   /// <summary>Cancels a waiting browser sign-in when the connection dialog closes, so no attempt keeps listening after the operator left.</summary>
   private void ConnectionDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args) => _signIn.CancelBrowserSignIn();



   /// <summary>Asks the entered server which sign-in methods it offers; the flow shows only those and reports a server that offers neither as needing configuration.</summary>
   private async void DiscoverSignIn_Click(object sender, RoutedEventArgs e)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         await _signIn.DiscoverAsync(ServerBox.Text);
      }
      finally
      {
         SetBusy(false);
      }
   }



   /// <summary>Signs in with the entered user name and password and the selected pairing choice. The password box is cleared whether or not sign-in worked.</summary>
   private async void PasswordSignIn_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || !_signIn.HasSession)
      {
         return;
      }
      SetBusy(true);
      try
      {
         await _signIn.SignInWithPasswordAsync(UsernameBox.Text, QqqPasswordBox.Password, PairingSelection.FromIndex(PairingChoice.SelectedIndex));
      }
      finally
      {
         QqqPasswordBox.Password = string.Empty;
         SetBusy(false);
      }
   }



   /// <summary>Runs the browser sign-in with the selected pairing choice; the flow shows the Cancel button while it waits for the browser.</summary>
   private async void BrowserSignIn_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || !_signIn.HasSession)
      {
         return;
      }
      SetBusy(true);
      try
      {
         await _signIn.SignInWithBrowserAsync(PairingSelection.FromIndex(PairingChoice.SelectedIndex));
      }
      finally
      {
         SetBusy(false);
      }
   }



   /// <summary>Asks the service to sign out and forget its session; the flow also finishes the identity provider's own sign-out when the server offers one. Pairing and job history are not removed.</summary>
   private async void SignOut_Click(object sender, RoutedEventArgs e)
   {
      if(_busy)
      {
         return;
      }
      SetBusy(true);
      try
      {
         if(!await _signIn.SignOutAsync())
         {
            ServiceUnavailable();
         }
      }
      finally
      {
         SetBusy(false);
      }
   }



   /// <summary>
   /// Enrolls this device through the service with the entered address and code; the window never receives or stores the
   /// device token. An incomplete form is refused before anything is sent, the service's confirmation becomes the status
   /// line, and the code box is cleared after every attempt.
   /// </summary>
   private async void Enroll_Click(object sender, RoutedEventArgs e)
   {
      if(_busy || !_signIn.CheckEnrollment(ServerBox.Text, EnrollmentBox.Password))
      {
         return;
      }
      SetBusy(true);
      try
      {
         EnrollmentResult result = await _signIn.EnrollAsync(ServerBox.Text, EnrollmentBox.Password);
         if(result.StatusText is string status)
         {
            StatusText.Text = status;
         }
         if(!result.ServiceReachable)
         {
            ServiceUnavailable();
         }
      }
      finally
      {
         EnrollmentBox.Password = string.Empty;
         SetBusy(false);
      }
   }
}
