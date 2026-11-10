/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The connection texts the window shows for one service snapshot: the footer's status sentence and contact line, the
/// server label and its link name, the connection button and dialog title, and the two summaries on the Settings page.
/// The footer sentence starts from the health label and is replaced by the more specific reason when the server paused
/// or disconnected dispatch or when sign-in is required, in that order of precedence.
/// </summary>
public sealed record ShellStatus(string FooterStatus, string FooterContact, string ServerLabel, string ServerLinkName,
   string ConnectionAction, string ConnectionDialogTitle, string ConnectionText, string AuthenticationText)
{
   /// <summary>The status line shown when the local service cannot be reached.</summary>
   public const string ServiceUnavailableStatus = "Local print service unavailable";

   /// <summary>The footer contact line shown when the local service cannot be reached, which says the rest of the screen may be stale.</summary>
   public const string ServiceUnavailableContact = "Local service unavailable · retrying automatically; shown data may be stale";

   /// <summary>The status line shown when the service answered a status request with an error.</summary>
   public const string ServiceProblemStatus = "Local service needs attention";



   /// <summary>Builds the texts for a snapshot, formatting times with the given format and naming the server the way this edition does.</summary>
   public static ShellStatus From(AgentSnapshot snapshot, TimestampFormat time, string serverName)
   {
      ArgumentNullException.ThrowIfNull(snapshot);
      ArgumentNullException.ThrowIfNull(time);
      string serverLabel = TransportFormatting.Server(snapshot.ServerUrl);
      string lastContact = time.Brief(snapshot.LastSuccessfulPoll);
      return new ShellStatus(
         Footer(snapshot, serverName),
         $"Last server contact: {lastContact} · Local status refreshes every 5 seconds" + ControlContact(snapshot.Control, time),
         serverLabel,
         "Open connection settings: " + serverLabel,
         snapshot.Enrolled ? "Sign in again" : "Connect this device",
         snapshot.Enrolled ? "Sign in again" : "Connect to the server",
         snapshot.Enrolled
            ? $"Server: {snapshot.ServerUrl}\nDevice: {snapshot.DeviceId}\nLast server contact: {lastContact}"
            : "This device has not been enrolled. Open Connection settings to connect it to the server.",
         Authentication(snapshot.Authentication, serverName));
   }



   /// <summary>The footer sentence: the health label, unless remote control or missing authorization gives a more exact reason. Missing authorization is read from the authentication status only; the control state no longer carries a demand of its own.</summary>
   private static string Footer(AgentSnapshot snapshot, string serverName)
   {
      string status = HealthStatus.From(snapshot.Health).Label;
      if(snapshot.Control is { } control)
      {
         if(string.Equals(control.DesiredState.ConnectionState, "DISCONNECTED", StringComparison.Ordinal))
         {
            status = "Dispatch disconnected by administrator";
         }
         else if(control.DesiredState.DispatchPaused)
         {
            status = "Dispatch paused by administrator";
         }
      }
      return snapshot.Authentication is { Authorized: false } ? serverName + " authorization required" : status;
   }



   /// <summary>The remote-control part of the contact line: whether the server supports remote controls, when they were last heard from, and the last command with its outcome on a second line.</summary>
   private static string ControlContact(AgentControlState? control, TimestampFormat time)
   {
      if(control is null)
      {
         return string.Empty;
      }
      string text = $" · Remote controls: {AgentControlLabels.Support(control.Support)} · Last control contact: {time.Brief(control.LastContact)}";
      return control.LastCommand is null ? text
         : text + $"\n{AgentControlLabels.Action(control.LastCommand)}: {AgentControlLabels.Outcome(control.LastOutcome)} · {time.Brief(control.LastCommandAt)}";
   }



   /// <summary>Who is signed in and the service's own sentence about it, or an invitation to sign in when the service reports no authentication at all.</summary>
   private static string Authentication(AgentAuthenticationStatus? authentication, string serverName)
   {
      if(authentication is null)
      {
         return $"Sign in to {serverName} to enable printing.";
      }
      return authentication.SignedIn ? $"Signed in as {authentication.PrincipalId} · Tenant {authentication.TenantId}\n{authentication.Message}" : authentication.Message;
   }
}
