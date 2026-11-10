/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Every message the connection dialog shows during discovery, sign-in, sign-out, enrollment and the certificate
/// exception. Failures quote the reason and point to the activity log; none of them says which part of a credential was
/// wrong, and none contains a password, code or token.
/// </summary>
public static class SignInMessages
{
   private const string LogHint = " The activity log has the full detail.";



   /// <summary>Discovery found at least one sign-in method this application supports.</summary>
   public static FeedbackMessage OptionsReady { get; } = new("Sign-in options ready", "Use the sign-in method configured by this server.", FeedbackSeverity.Informational);



   /// <summary>Discovery worked, but the server offers neither password sign-in nor the native browser grant.</summary>
   public static FeedbackMessage ConfigurationRequired { get; } = new("Server configuration required", "This server has not enabled a supported native sign-in grant. Ask its administrator to configure it.", FeedbackSeverity.Warning);



   /// <summary>A password sign-in was sent to the server.</summary>
   public static FeedbackMessage SigningIn { get; } = new("Signing in", "Checking the username and password with the server.", FeedbackSeverity.Informational);



   /// <summary>The provider's page was opened and the dialog is waiting, with the three-minute limit stated.</summary>
   public static FeedbackMessage WaitingForBrowser { get; } = new("Waiting for the browser", "Finish signing in on the page that opened in your browser. This dialog updates when the sign-in completes, is canceled or times out after three minutes.", FeedbackSeverity.Informational);



   /// <summary>The browser sign-in was cancelled or timed out before anything was handed to the service; the half-finished attempt was discarded.</summary>
   public static FeedbackMessage BrowserCanceled { get; } = new("Browser sign-in canceled", "No new sign-in was accepted. Choose Discover sign-in options to start a new attempt.", FeedbackSeverity.Informational);



   /// <summary>The enrollment form is incomplete or names an address that is not allowed.</summary>
   public static FeedbackMessage CheckEnrollmentDetails { get; } = new("Check the connection details", "Enter an HTTPS server address and the enrollment code from the server. Localhost HTTP is supported for development.", FeedbackSeverity.Warning);



   /// <summary>The enrollment request was sent to the service.</summary>
   public static FeedbackMessage Enrolling { get; } = new("Connecting", "The service is enrolling this device with the server…", FeedbackSeverity.Informational);



   /// <summary>An operation was asked for while another one was still running. It was not started, and nothing the operator entered was used or cleared.</summary>
   public static FeedbackMessage Busy { get; } = new("Another operation is still running", "This request was not started. Wait for the current operation to finish, then try again.", FeedbackSeverity.Warning);



   /// <summary>The certificate exception was requested before a server address was entered.</summary>
   public static FeedbackMessage EnterAddressFirst { get; } = new("Enter the server address first", "The certificate exception is saved for one server only, so the server address must be entered before it can be allowed.", FeedbackSeverity.Warning);



   /// <summary>The certificate exception was turned off again.</summary>
   public static FeedbackMessage TrustRefused { get; } = new("Untrusted certificates refused", "Only certificates Windows trusts for the service account are accepted again.", FeedbackSeverity.Informational);



   /// <summary>Discovery failed, with the reason.</summary>
   public static FeedbackMessage DiscoveryFailed(string reason)
      => new("Discovery failed", "The server did not provide compatible authentication and printing discovery. Reason: " + reason + LogHint, FeedbackSeverity.Error);



   /// <summary>A password sign-in did not complete. The message deliberately does not say whether the credentials, the device or a permission was the problem.</summary>
   public static FeedbackMessage PasswordNotCompleted(string reason)
      => new("Sign-in not completed", "The server did not authorize this connection, or the service could not confirm it. Reason: " + reason + LogHint, FeedbackSeverity.Error);



   /// <summary>A browser sign-in did not complete, with the reason and what to check before trying again.</summary>
   public static FeedbackMessage BrowserNotCompleted(string reason)
      => new("Browser sign-in not completed", "No validated account session was received. Reason: " + reason + " Check the server's advertised native sign-in grant and installed callback configuration, then try again explicitly." + LogHint, FeedbackSeverity.Warning);



   /// <summary>
   /// Says what really happened to a request that left for the service and brought no usable answer back, in one
   /// sentence: its time limit ran out, the connection closed, the answer was too large or could not be read, or the wait
   /// was ended. The messages that follow an unconfirmed request start with this sentence, so none of them claims a time
   /// limit that did not happen.
   /// </summary>
   public static string Unanswered<T>(ServiceResult<T> result)
   {
      ArgumentNullException.ThrowIfNull(result);
      return result switch
      {
         { Outcome: ServiceOutcome.Canceled } => "The wait for the service's answer was ended.",
         { Failure: ServiceFailure.TimedOut } => "The request timed out.",
         { Failure: ServiceFailure.Disconnected } => "The connection to the service closed before its answer arrived.",
         { Failure: ServiceFailure.InvalidData } => "The service's answer was larger than the companion accepts.",
         { Failure: ServiceFailure.UnreadableReply } => "The service answered, but its answer could not be read.",
         _ => "The service's answer did not arrive. Reason: " + result.Message,
      };
   }



   /// <summary>
   /// The session was sent to the service and no usable answer came back, so the service may have signed in or not. The
   /// operator is told to look at the status before trying again, and how to start over when it shows no sign-in.
   /// </summary>
   public static FeedbackMessage HandOverUnconfirmed(string cause)
      => new("Sign-in could not be confirmed", cause + " The sign-in was sent to the service, which may still complete it. Check the connection status before trying again; if it does not show this account as signed in, choose Discover sign-in options and sign in again." + LogHint, FeedbackSeverity.Warning);



   /// <summary>The enrollment request left and no usable answer came back, so the code may have been used; the operator must check the server before trying again.</summary>
   public static FeedbackMessage EnrollmentUnconfirmed(string cause)
      => new("Enrollment could not be confirmed", cause + " Check the device list in the server before trying again: the code may have been consumed. Revoke any unused device created by this attempt, then generate a fresh enrollment code. Your prior connection remains available unless this enrollment completed.", FeedbackSeverity.Warning);



   /// <summary>The sign-out request left and no usable answer came back, so the session may have ended or not; this is not the same as the service being unavailable.</summary>
   public static FeedbackMessage SignOutUnconfirmed(string cause)
      => new("Sign-out could not be confirmed", cause + " The session may have ended. Check the connection status before trying again." + LogHint, FeedbackSeverity.Warning);



   /// <summary>The service's answer to a handed-over session: success only when the service confirmed it signed in, otherwise its own sentence as an error.</summary>
   public static FeedbackMessage HandedOver(string reply, bool confirmed)
      => new(confirmed ? "Signed in" : "Service authorization not completed", reply, confirmed ? FeedbackSeverity.Success : FeedbackSeverity.Error);



   /// <summary>The service's answer to a sign-out, as a warning when the service reported an error.</summary>
   public static FeedbackMessage SignedOut(string message, bool rejected)
      => new("Account sign-out", message, rejected ? FeedbackSeverity.Warning : FeedbackSeverity.Informational);



   /// <summary>The service signed out and the identity provider's own sign-out page was opened in the browser.</summary>
   public static FeedbackMessage SigningOutOfProvider(string message, bool rejected)
      => SignedOut(message + " Finish signing out of the identity provider on the page that opened in your browser.", rejected);



   /// <summary>Whether the identity provider confirmed its sign-out, and what to do when it did not.</summary>
   public static FeedbackMessage ProviderSignedOut(string message, bool confirmed) => confirmed
      ? new("Account sign-out", message + " The identity provider session ended too, so the next sign-in asks for credentials.", FeedbackSeverity.Success)
      : new("Account sign-out", message + " The identity provider sign-out was opened in your browser but did not confirm; close that page and sign out there if it is still open.", FeedbackSeverity.Warning);



   /// <summary>The service refused the enrollment; its reply is shown whole, followed by what to do and that the previous connection is kept.</summary>
   public static FeedbackMessage EnrollmentFailed(string reply)
      => new("Enrollment failed", reply + " If the code expired or was already used, generate a fresh code in the server. Your previous connection is retained unless enrollment completes.", FeedbackSeverity.Error);



   /// <summary>The device was enrolled; the next steps are sharing printers here and approving them on the server.</summary>
   public static FeedbackMessage DeviceConnected(string reply)
      => new("Device connected", reply + ". Select which printers to expose here, then approve the discovered queues in the server.", FeedbackSeverity.Success);



   /// <summary>The service refused to save the certificate exception; its reply is shown whole.</summary>
   public static FeedbackMessage TrustNotSaved(string reply) => new("Server trust not saved", reply, FeedbackSeverity.Error);



   /// <summary>The certificate exception was saved for one server, with the warning that any certificate from it is now accepted.</summary>
   public static FeedbackMessage TrustAllowed(string? origin)
      => new("Untrusted certificate allowed for " + origin, "The service and companion will accept any certificate from this one server until this is turned off. Other servers keep normal validation. Use only with a development server.", FeedbackSeverity.Warning);



   /// <summary>The label of the certificate-exception checkbox, which names the server a saved exception is bound to, so an exception saved for another server is visibly not in force for the one being entered.</summary>
   public static string TrustLabel(ServerTrust trust)
   {
      ArgumentNullException.ThrowIfNull(trust);
      return trust.AllowUntrustedCertificate
         ? "Allow untrusted server certificate for " + trust.Origin + " (development only)"
         : "Allow untrusted server certificate for this server (development only)";
   }
}
