/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>The footer for the whole computer: the worst health, the count in each state, the first server that needs attention, and with one server that server's own sentence, which is what 1.x said.</summary>
public sealed class FooterStatusTests
{
   /// <summary>A service that answered with its stored data ready.</summary>
   private static readonly ServiceAvailability s_reached = new(ServiceAvailabilityKind.Reached, "The local print service is running.", string.Empty);



   /// <summary>The footer for these servers of a service that answered.</summary>
   private static FooterStatus Footer(params ServerSnapshot[] servers) => FooterStatus.From(s_reached, [.. servers.Select(server => ServerStatus.From(server, [Snapshots.Printer()]))]);



   /// <summary>
   /// With several servers the footer shows the worst health, says how many servers are in each state, worst
   /// first and only the states that occur, and points at the first server in the worst state. Red is worst; then
   /// unknown, which is never hidden behind orange or green; then orange. When every server is green it points at
   /// nothing.
   /// </summary>
   [Fact]
   public void Footer_WorstHealth_CountPerState_FirstServerNeedingAttention()
   {
      FooterStatus mixed = Footer(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Orange, transport: "CONNECTING"), Snapshots.Server(3, "Dock", health: ServerSnapshot.Red),
         Snapshots.Server(4, health: "PURPLE"), Snapshots.Server(5, health: ServerSnapshot.Red), Snapshots.Server(6));

      Assert.Equal(HealthBadge.For(HealthLevel.Red), mixed.Health);
      Assert.Equal("6 servers: 2 need attention, 1 unknown, 1 not ready, 2 ready", mixed.Text);
      Assert.Equal((2, 1, 1, 2), (mixed.NeedAttention, mixed.Unknown, mixed.NotReady, mixed.Ready));
      Assert.Equal(PageKey.Server(Snapshots.Id(3)), mixed.Attention);
      Assert.Equal("Open Dock", mixed.AttentionText);

      FooterStatus unknown = Footer(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Orange, transport: "CONNECTING"), Snapshots.Server(3, health: "PURPLE"));
      Assert.Equal(HealthLevel.Unknown, unknown.Health.Level);
      Assert.Equal("3 servers: 1 unknown, 1 not ready, 1 ready", unknown.Text);
      Assert.Equal(PageKey.Server(Snapshots.Id(3)), unknown.Attention);

      FooterStatus orange = Footer(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Orange, transport: "CONNECTING"), Snapshots.Server(3, health: ServerSnapshot.Orange, transport: "CONNECTING"));
      Assert.Equal(HealthLevel.Orange, orange.Health.Level);
      Assert.Equal("3 servers: 2 not ready, 1 ready", orange.Text);
      Assert.Equal(PageKey.Server(Snapshots.Id(2)), orange.Attention);
      Assert.Equal("Open Server 2", orange.AttentionText);

      FooterStatus oneRed = Footer(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Red));
      Assert.Equal("2 servers: 1 needs attention, 1 ready", oneRed.Text);

      FooterStatus green = Footer(Snapshots.Server(1), Snapshots.Server(2));
      Assert.Equal(HealthBadge.For(HealthLevel.Green), green.Health);
      Assert.Equal("2 servers: 2 ready", green.Text);
      Assert.Equal((0, 0, 0, 2), (green.NeedAttention, green.Unknown, green.NotReady, green.Ready));
      Assert.Null(green.Attention);
      Assert.Null(green.AttentionText);
   }



   /// <summary>The footer's link opens the page it points at, and does nothing while it points at nothing.</summary>
   [Fact]
   public async Task Footer_Attention_OpensTheFirstServerThatNeedsIt()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Red), Snapshots.Server(3, health: ServerSnapshot.Red));

      Assert.True(fixture.Shell.SelectAttention());
      Assert.Equal(PageKey.Server(Snapshots.Id(2)), fixture.Shell.Selected);

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2), Snapshots.Server(3));
      fixture.Shell.Select(PageKey.Logs);

      Assert.False(fixture.Shell.SelectAttention());
      Assert.Equal(PageKey.Logs, fixture.Shell.Selected);
   }



   /// <summary>
   /// With one server the footer is that server's own sentence, and for an operator who upgraded from 1.x with one
   /// paired server whose service answers it is the sentence the 1.x footer showed in these eleven states: the same
   /// inputs are given to the 1.x texts and to the footer, for every health, with dispatch running, paused and
   /// disconnected, authorized and not. The 1.x side is given "Warehouse" as the edition's name, which hides the
   /// difference in "authorization required": 1.x named the edition, and this names the server. What else an
   /// upgraded operator reads differently (before the first answer, while the service is not answering, and in the
   /// states 1.x did not have) is listed in ADR-0005.
   /// </summary>
   [Theory]
   [InlineData("GREEN", false, "CONNECTED", true, "Connected and ready")]
   [InlineData("ORANGE", false, "CONNECTED", true, "Connected · printers need attention")]
   [InlineData("RED", false, "CONNECTED", true, "Disconnected or needs attention")]
   [InlineData("ORANGE", true, "CONNECTED", true, "Dispatch paused by administrator")]
   [InlineData("RED", true, "CONNECTED", true, "Dispatch paused by administrator")]
   [InlineData("ORANGE", false, "DISCONNECTED", true, "Dispatch disconnected by administrator")]
   [InlineData("RED", true, "DISCONNECTED", true, "Dispatch disconnected by administrator")]
   [InlineData("RED", false, "CONNECTED", false, "Warehouse authorization required")]
   [InlineData("RED", true, "CONNECTED", false, "Warehouse authorization required")]
   [InlineData("RED", false, "DISCONNECTED", false, "Warehouse authorization required")]
   [InlineData("GREEN", false, "CONNECTED", false, "Warehouse authorization required")]
   public async Task Footer_OneServer_IsThatServersSentence(string health, bool paused, string connection, bool authorized, string expected)
   {
      AgentSnapshot before = TestData.Snapshot(health: health, control: new AgentControlState("SUPPORTED", new DeviceDesiredState(3, paused, connection)),
         authentication: new AgentAuthenticationStatus(true, "principal-4", "tenant-2", "Signed in.", authorized));
      string footerOfOnePointX = ShellStatus.From(before, TestData.Time, "Warehouse").FooterStatus;
      using ShellFixture fixture = new();

      await fixture.Tick(Snapshots.Server(1, "Warehouse", health: health, paused: paused, connection: connection, authorized: authorized, shared: []));

      Assert.Equal(expected, footerOfOnePointX);
      Assert.Equal(footerOfOnePointX, fixture.Shell.Footer.Text);
      Assert.Equal(HealthStatus.LevelOf(health), fixture.Shell.Footer.Health.Level);
      Assert.Equal(HealthStatus.From(health).Level, fixture.Shell.Footer.Health.Level);
      Assert.Equal(fixture.Shell.Footer.Text, fixture.Shell.Tray.StatusLine);
      Assert.Equal(string.Equals(health, "GREEN", StringComparison.Ordinal) ? null : PageKey.Server(Snapshots.Id(1)), fixture.Shell.Footer.Attention);
   }



   /// <summary>
   /// The node of a server that is not authorized to print says what is missing without repeating the name it
   /// stands beside: "Sign in required" when nobody is signed in, "Authorization required" when somebody is and the
   /// session is not authorized. Alone in the footer both are the 1.x sentence, which names the server.
   /// </summary>
   [Fact]
   public void Footer_OneServer_NotAuthorized_NamesTheServer_TheNodeDoesNot()
   {
      var status = ServerStatus.From(Snapshots.Server(1, "Warehouse", authorized: false, health: ServerSnapshot.Red), []);
      var signedOut = ServerStatus.From(Snapshots.Server(1, "Warehouse", signedIn: false, authorized: false, health: ServerSnapshot.Red), []);

      Assert.Equal("Authorization required", status.Sentence);
      Assert.Equal("Warehouse authorization required", status.Alone);
      Assert.Equal("Sign in required", signedOut.Sentence);
      Assert.Equal("Warehouse authorization required", signedOut.Alone);
      Assert.Equal("Connected and ready", ServerStatus.From(Snapshots.Server(), [Snapshots.Printer()]).Alone);
      Assert.Equal("Open Warehouse", FooterStatus.From(s_reached, [status]).AttentionText);
   }



   /// <summary>
   /// While printing is paused for an update a computer that has servers says so, whatever the servers say, and is
   /// never green; the link opens the Computer page, which says until when, and the counts stay the servers' own.
   /// A computer with no server still says to add one.
   /// </summary>
   [Theory]
   [InlineData("GREEN", HealthLevel.Orange)]
   [InlineData("ORANGE", HealthLevel.Orange)]
   [InlineData("PURPLE", HealthLevel.Unknown)]
   [InlineData("RED", HealthLevel.Red)]
   public void Footer_WhileAnUpdatePausesPrinting_SaysSo_IsNeverGreen(string health, HealthLevel expected)
   {
      ServiceAvailability paused = new(ServiceAvailabilityKind.UpdatePaused, "Printing is paused while an update is installed.", "The pause ends when the update has finished.");
      ServerStatus[] servers = [ServerStatus.From(Snapshots.Server(1, "Warehouse", health: health), [Snapshots.Printer()]), ServerStatus.From(Snapshots.Server(2, "Bench"), [Snapshots.Printer()])];

      var one = FooterStatus.From(paused, [servers[0]]);
      var two = FooterStatus.From(paused, servers);

      Assert.Equal(new FooterStatus(HealthBadge.For(expected), "Printing paused while an update is installed", one.NeedAttention, one.Unknown, one.NotReady, one.Ready, PageKey.Computer, "Open Computer"), one);
      Assert.Equal(1, one.NeedAttention + one.Unknown + one.NotReady + one.Ready);
      Assert.Equal((expected, "Printing paused while an update is installed", PageKey.Computer), (two.Health.Level, two.Text, two.Attention));
      Assert.Equal(2, two.NeedAttention + two.Unknown + two.NotReady + two.Ready);
      Assert.Equal(new FooterStatus(HealthBadge.For(HealthLevel.Red), "Add a server to start printing", 0, 0, 0, 0, PageKey.Wizard, "Add server"), FooterStatus.From(paused, []));
   }



   /// <summary>With no server the footer is red and says to add one, as 1.x is red before a computer is connected; its link opens the wizard.</summary>
   [Fact]
   public async Task Footer_NoServers_IsRed_SaysToAddAServer()
   {
      using ShellFixture fixture = new();

      await fixture.Tick();

      Assert.Equal(new FooterStatus(HealthBadge.For(HealthLevel.Red), "Add a server to start printing", 0, 0, 0, 0, PageKey.Wizard, "Add server"), fixture.Shell.Footer);
      Assert.Equal("red", fixture.Shell.Tray.IconName);
   }



   /// <summary>
   /// While the service has not answered, or its stored data is not ready, the footer says that, is red, and points
   /// at the Computer page, which says why. Before the first read has ended the health is unknown, not red and not
   /// healthy. Three of the sentences are the ones 1.x showed.
   /// </summary>
   [Theory]
   [InlineData(ServiceAvailabilityKind.NotReached, "Local print service unavailable")]
   [InlineData(ServiceAvailabilityKind.OlderService, "Windows service is older than this companion")]
   [InlineData(ServiceAvailabilityKind.NewerService, "Windows service is newer than this companion")]
   [InlineData(ServiceAvailabilityKind.Unreadable, "Cannot read the local service's answer")]
   [InlineData(ServiceAvailabilityKind.Refused, "Local service needs attention")]
   [InlineData(ServiceAvailabilityKind.UpgradeIncomplete, "Upgrade not finished · not printing")]
   [InlineData(ServiceAvailabilityKind.NewerBuild, "Stored data is from a newer build · not printing")]
   [InlineData(ServiceAvailabilityKind.StoredDataNotReady, "Stored data not ready · not printing")]
   public void Footer_ServiceNotAvailable_SaysSo_IsRed_PointsAtComputer(ServiceAvailabilityKind kind, string expected)
   {
      var footer = FooterStatus.From(new ServiceAvailability(kind, "Summary.", "Guidance."), [ServerStatus.From(Snapshots.Server(), [], current: false)]);

      Assert.Equal(expected, footer.Text);
      Assert.Equal(HealthBadge.For(HealthLevel.Red), footer.Health);
      Assert.Equal((0, 1, 0, 0), (footer.NeedAttention, footer.Unknown, footer.NotReady, footer.Ready));
      Assert.Equal((PageKey.Computer, "Open Computer"), (footer.Attention, footer.AttentionText));
      Assert.Equal(ShellStatus.ServiceUnavailableStatus, ShellMessages.FooterNotReached);
      Assert.Equal(ShellStatus.ServiceProblemStatus, ShellMessages.FooterRefused);
   }



   /// <summary>Before the first read has ended the footer says it is checking, its health is unknown and it points at nothing.</summary>
   [Fact]
   public void Footer_BeforeTheFirstRead_IsChecking_AndUnknown()
   {
      using ShellFixture fixture = new();

      Assert.Equal(new FooterStatus(HealthBadge.For(HealthLevel.Unknown), "Checking local service…", 0, 0, 0, 0, null, null), fixture.Shell.Footer);
      Assert.Equal("unknown", fixture.Shell.Tray.IconName);
      Assert.Equal("", fixture.Shell.FooterAnnouncement.Text);
      Assert.Empty(fixture.Announced);
      Assert.Throws<ArgumentNullException>(() => FooterStatus.From(null!, []));
      Assert.Throws<ArgumentNullException>(() => FooterStatus.From(ServiceAvailability.Checking, null!));
   }



   /// <summary>
   /// The footer's sentence is announced once for each change, after the footer holds it: at the moment of the
   /// announcement the footer, the tray and the tree already show the new state. A tick that changes nothing, and a
   /// read that was skipped, announce nothing.
   /// </summary>
   [Fact]
   public async Task FooterText_IsFinal_BeforeItIsAnnounced_Once()
   {
      using ShellFixture fixture = new();
      List<string> order = [];
      fixture.Shell.PropertyChanged += (_, change) => order.Add("changed " + change.PropertyName);
      fixture.Shell.FooterAnnouncement.Raised += (_, _) => order.Add("announced; tray: " + fixture.Shell.Tray.StatusLine + "; node: " + fixture.Shell.Tree.Servers[0].StatusText + "; page: " + fixture.Shell.CurrentPage.Title);

      await fixture.Tick(Snapshots.Server(1, "Warehouse"));

      Assert.Equal(["Connected and ready | footer: Connected and ready"], fixture.Announced);
      Assert.Equal("announced; tray: Connected and ready; node: Connected and ready; page: Printers", order[^1]);
      Assert.True(order.IndexOf("changed Footer") < order.Count - 1);
      Assert.Single(order, entry => entry.StartsWith("announced", StringComparison.Ordinal));

      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      await fixture.Tick(Snapshots.Server(1, "Warehouse east"));
      Assert.Single(fixture.Announced);

      await fixture.Tick(Snapshots.Server(1, "Warehouse", paused: true, health: ServerSnapshot.Orange));
      Assert.Equal(["Connected and ready | footer: Connected and ready", "Dispatch paused by administrator | footer: Dispatch paused by administrator"], fixture.Announced);

      await fixture.TickFailing(new IOException("The pipe is broken."));
      await fixture.TickFailing(new IOException("The pipe is broken."));
      Assert.Equal(3, fixture.Announced.Count);
      Assert.Equal("Local print service unavailable | footer: Local print service unavailable", fixture.Announced[2]);

      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      Assert.Equal(4, fixture.Announced.Count);
      Assert.Equal("Connected and ready | footer: Connected and ready", fixture.Announced[3]);
      Assert.Equal("Connected and ready", fixture.Shell.FooterAnnouncement.Text);
   }
}
