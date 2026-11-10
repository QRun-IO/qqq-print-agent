/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>The footer, tray and Settings connection texts for a snapshot from the service.</summary>
public sealed class ShellStatusTests
{
   /// <summary>Builds a remote-control state with the given desired state and optional last command.</summary>
   private static AgentControlState Control(bool paused = false, string connection = "CONNECTED", string? lastCommand = null)
      => new("SUPPORTED", new DeviceDesiredState(3, paused, connection), "2026-11-10T09:59:00Z", lastCommand, lastCommand is null ? null : "INVENTORY_REFRESHED", lastCommand is null ? null : "2026-11-10T09:58:00Z");



   /// <summary>Each health word has a color level, a sentence and a tray icon; a word this build does not know, or none, is red.</summary>
   [Theory]
   [InlineData("GREEN", HealthLevel.Green, "Connected and ready", "green")]
   [InlineData("ORANGE", HealthLevel.Orange, "Connected · printers need attention", "orange")]
   [InlineData("RED", HealthLevel.Red, "Disconnected or needs attention", "red")]
   [InlineData("PURPLE", HealthLevel.Red, "Disconnected or needs attention", "red")]
   [InlineData(null, HealthLevel.Red, "Disconnected or needs attention", "red")]
   public void Health_PairsColorWithWords(string? health, HealthLevel level, string label, string icon)
      => Assert.Equal(new HealthStatus(level, label, icon), HealthStatus.From(health));



   /// <summary>The tray tooltip is the status and the product name, and a long one is cut to the 127 characters Windows accepts with an ellipsis, never in the middle of a surrogate pair.</summary>
   [Fact]
   public void TrayToolTip_FitsTheWindowsLimitWithoutSplittingACharacter()
   {
      Assert.Equal("Connected and ready — QQQ Print Agent", HealthStatus.TrayToolTip("Connected and ready", "QQQ Print Agent"));

      string plain = HealthStatus.TrayToolTip("Disconnected or needs attention", new string('x', 200));
      Assert.Equal(127, plain.Length);
      Assert.EndsWith("x…", plain, StringComparison.Ordinal);

      string status = new('s', 122);
      string split = HealthStatus.TrayToolTip(status, "😀 Printing");
      Assert.Equal(status + " — …", split);
      Assert.False(char.IsHighSurrogate(split[^2]));
   }



   /// <summary>An enrolled, healthy device shows the health sentence, the last contact in local time, only the server's host in the footer, and offers to sign in again; without authentication details it invites sign-in by the edition's server name.</summary>
   [Fact]
   public void EnrolledDevice_ShowsHealthContactAndServer()
   {
      var status = ShellStatus.From(TestData.Snapshot(serverUrl: "https://print.example.com/base?token=private"), TestData.Time, "QQQ");

      Assert.Equal("Connected and ready", status.FooterStatus);
      Assert.Equal("Last server contact: 11/10/2026 12:00 · Local status refreshes every 5 seconds", status.FooterContact);
      Assert.Equal("print.example.com", status.ServerLabel);
      Assert.Equal("Open connection settings: print.example.com", status.ServerLinkName);
      Assert.Equal("Sign in again", status.ConnectionAction);
      Assert.Equal("Sign in again", status.ConnectionDialogTitle);
      Assert.Equal("Server: https://print.example.com/base?token=private\nDevice: device-1\nLast server contact: 11/10/2026 12:00", status.ConnectionText);
      Assert.Equal("Sign in to QQQ to enable printing.", status.AuthenticationText);
   }



   /// <summary>A device that is not enrolled offers to connect, titles the dialog accordingly, and says where to connect it; a contact that never happened reads "Not yet".</summary>
   [Fact]
   public void UnenrolledDevice_OffersToConnect()
   {
      var status = ShellStatus.From(TestData.Snapshot(enrolled: false, health: "RED", serverUrl: null, lastPoll: null), TestData.Time, "QQQ");

      Assert.Equal("Disconnected or needs attention", status.FooterStatus);
      Assert.Equal("Last server contact: Not yet · Local status refreshes every 5 seconds", status.FooterContact);
      Assert.Equal("Connection settings", status.ServerLabel);
      Assert.Equal("Connect this device", status.ConnectionAction);
      Assert.Equal("Connect to the server", status.ConnectionDialogTitle);
      Assert.Equal("This device has not been enrolled. Open Connection settings to connect it to the server.", status.ConnectionText);
   }



   /// <summary>Remote control replaces the health sentence with the exact reason dispatch is not running: an administrator's disconnect first, then a pause; a running device keeps the health sentence.</summary>
   [Theory]
   [InlineData("DISCONNECTED", true, "Dispatch disconnected by administrator")]
   [InlineData("CONNECTED", true, "Dispatch paused by administrator")]
   [InlineData("CONNECTED", false, "Connected and ready")]
   public void RemoteControl_GivesTheExactReasonDispatchIsNotRunning(string connection, bool paused, string expected)
   {
      var status = ShellStatus.From(TestData.Snapshot(control: Control(paused, connection)), TestData.Time, "QQQ");

      Assert.Equal(expected, status.FooterStatus);
      Assert.Equal("Last server contact: 11/10/2026 12:00 · Local status refreshes every 5 seconds · Remote controls: available · Last control contact: 11/10/2026 11:59", status.FooterContact);
   }



   /// <summary>The last remote command and its outcome appear on a second line of the contact text.</summary>
   [Fact]
   public void LastRemoteCommand_IsShownWithItsOutcome()
   {
      var status = ShellStatus.From(TestData.Snapshot(control: Control(lastCommand: "REFRESH_INVENTORY")), TestData.Time, "QQQ");

      Assert.EndsWith(" · Last control contact: 11/10/2026 11:59\nRefresh printers: Printers refreshed · 11/10/2026 11:58", status.FooterContact, StringComparison.Ordinal);
   }



   /// <summary>A signed-in user is named with the tenant and the service's sentence; a session that is not authorized overrides every other footer reason with the edition's server name; a signed-out service shows only its own sentence.</summary>
   [Fact]
   public void Authentication_NamesTheUserAndOverridesTheFooterWhenNotAuthorized()
   {
      var authorized = ShellStatus.From(TestData.Snapshot(authentication: new AgentAuthenticationStatus(true, "operator@example.com", "tenant-7", "Signed in to Forge.", Authorized: true)), TestData.Time, "Forge");
      var unauthorized = ShellStatus.From(TestData.Snapshot(control: Control(paused: true), authentication: new AgentAuthenticationStatus(false, null, null, "Sign in to enable printing.")), TestData.Time, "Forge");

      Assert.Equal("Signed in as operator@example.com · Tenant tenant-7\nSigned in to Forge.", authorized.AuthenticationText);
      Assert.Equal("Connected and ready", authorized.FooterStatus);
      Assert.Equal("Sign in to enable printing.", unauthorized.AuthenticationText);
      Assert.Equal("Forge authorization required", unauthorized.FooterStatus);
   }



   /// <summary>The fixed texts for an unreachable or failing service say that the screen may be stale and that the service needs attention.</summary>
   [Fact]
   public void ServiceTrouble_HasFixedStatusTexts()
   {
      Assert.Equal("Local print service unavailable", ShellStatus.ServiceUnavailableStatus);
      Assert.Equal("Local service unavailable · retrying automatically; shown data may be stale", ShellStatus.ServiceUnavailableContact);
      Assert.Equal("Local service needs attention", ShellStatus.ServiceProblemStatus);
   }



   /// <summary>Each navigation choice maps to a page name and a one-sentence description; the built-in settings item is Settings and a choice without a tag is Printers.</summary>
   [Fact]
   public void Pages_HaveANameAndADescription()
   {
      Assert.Equal("Settings", ShellPages.Name(settingsSelected: true, tag: "Jobs"));
      Assert.Equal("Jobs", ShellPages.Name(settingsSelected: false, tag: "Jobs"));
      Assert.Equal("Printers", ShellPages.Name(settingsSelected: false, tag: null));
      Assert.Equal("Recent jobs received by this computer.", ShellPages.Description(ShellPages.Jobs));
      Assert.Equal("Activity and diagnostics in the AppKit log viewer.", ShellPages.Description(ShellPages.Logs));
      Assert.Equal("Connection, appearance and application information.", ShellPages.Description(ShellPages.Settings));
      Assert.Equal("Choose which printers this computer shares with the server.", ShellPages.Description(ShellPages.Printers));
   }
}
