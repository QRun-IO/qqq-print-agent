/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Specialized;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>The tree as the snapshots make it: one node per server in the snapshot's order, reconciled by identifier, with selection and expansion staying with the server.</summary>
public sealed class ServerTreeTests
{
   /// <summary>The three pages of an expanded server, as <see cref="ShellFixture.TreeIds"/> writes them.</summary>
   private const string Pages = "[NavigatePrinters,NavigateJobs,NavigateConnection]";

   /// <summary>The four nodes after the servers.</summary>
   private const string Rest = " AddServer NavigateComputer NavigateLogs NavigateSettings";



   /// <summary>Before anything was read the tree is Add server, Computer, Logs and Settings, with Computer selected.</summary>
   [Fact]
   public void Tree_BeforeAnyRead_HasTheFourFixedNodes_ComputerSelected()
   {
      using ShellFixture fixture = new();

      Assert.Equal(Rest.TrimStart(), fixture.TreeIds);
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.True(fixture.Shell.Tree.Computer.IsSelected);
      Assert.Empty(fixture.Shell.Tree.Servers);
   }



   /// <summary>Each server of the snapshot has one node, in the snapshot's order, with Printers, Jobs and Connection beneath it, each showing that server's page; then come the four fixed nodes.</summary>
   [Fact]
   public async Task Tree_OneNodePerServer_InOrder_WithPrintersJobsConnection()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));

      Assert.Equal("Server.1" + Pages + " Server.2" + Pages + Rest, fixture.TreeIds);
      Assert.Equal("Warehouse,Bench", fixture.ServerNames);
      ServerNode bench = fixture.Shell.Tree.Servers[1];
      Assert.Equal(Snapshots.Id(2), bench.ServerId);
      Assert.Equal(PageKey.Server(Snapshots.Id(2)), bench.Key);
      Assert.Equal([PageKind.Printers, PageKind.Jobs, PageKind.Connection], bench.Children.Select(page => page.Key.Kind));
      Assert.All(bench.Children, page => Assert.Equal(Snapshots.Id(2), page.Key.ServerId));
      Assert.Equal(["Printers", "Jobs", "Connection"], bench.Children.Select(page => page.Text), StringComparer.Ordinal);
      Assert.Same(bench, fixture.Shell.Tree.Server(Snapshots.Id(2)));
      Assert.Same(bench.Children[1], fixture.Shell.Tree.Find(PageKey.ForServer(PageKind.Jobs, Snapshots.Id(2))));
      Assert.Null(fixture.Shell.Tree.Server(Snapshots.Id(9)));
      Assert.Null(fixture.Shell.Tree.Server(null));
      Assert.Null(fixture.Shell.Tree.Find(PageKey.ForServer(PageKind.Jobs, Snapshots.Id(9))));
   }



   /// <summary>
   /// A server is <c>Server.&lt;n&gt;</c> by its place in the snapshot's list counted from one, whatever the snapshot's
   /// own position member says (it counts from zero). When the order changes or a server goes, the identifiers are
   /// renumbered, and each server keeps the node it had.
   /// </summary>
   [Fact]
   public async Task Tree_Identifiers_AreServerDotN_FromOne_RenumberedAfterReorderAndRemoval()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2), Snapshots.Server(3));
      ServerNode[] first = [.. fixture.Shell.Tree.Servers];
      Assert.Equal(["Server.1", "Server.2", "Server.3"], first.Select(node => node.AutomationId), StringComparer.Ordinal);
      Assert.Equal(0, Snapshots.Server(1).Position);

      await fixture.Tick(Snapshots.Server(3), Snapshots.Server(1), Snapshots.Server(2));

      Assert.Equal("Server 3,Server 1,Server 2", fixture.ServerNames);
      Assert.Equal(["Server.1", "Server.2", "Server.3"], fixture.Shell.Tree.Servers.Select(node => node.AutomationId), StringComparer.Ordinal);
      Assert.Same(first[2], fixture.Shell.Tree.Servers[0]);
      Assert.Same(first[0], fixture.Shell.Tree.Servers[1]);

      await fixture.Tick(Snapshots.Server(3), Snapshots.Server(2));

      Assert.Equal("Server 3,Server 2", fixture.ServerNames);
      Assert.Equal(["Server.1", "Server.2"], fixture.Shell.Tree.Servers.Select(node => node.AutomationId), StringComparer.Ordinal);
      Assert.Equal("Server.1" + Pages + " Server.2" + Pages + Rest, fixture.TreeIds);
      Assert.Same(first[1], fixture.Shell.Tree.Servers[1]);
   }



   /// <summary>The nodes are put in the snapshot's order by moving, inserting and removing them, and a snapshot that changes nothing tells the view nothing: the list is never emptied and filled again.</summary>
   [Fact]
   public async Task Tree_Reconcile_MovesInsertsAndRemoves_NeverResets()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      List<NotifyCollectionChangedAction> changes = [];
      fixture.Shell.Tree.Nodes.CollectionChanged += (_, change) => changes.Add(change.Action);

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      Assert.Empty(changes);

      await fixture.Tick(Snapshots.Server(2), Snapshots.Server(1));
      Assert.Equal([NotifyCollectionChangedAction.Move], changes);

      await fixture.Tick(Snapshots.Server(3), Snapshots.Server(2));
      Assert.Equal([NotifyCollectionChangedAction.Move, NotifyCollectionChangedAction.Remove, NotifyCollectionChangedAction.Add], changes);
      Assert.Equal("Server 3,Server 2", fixture.ServerNames);
   }



   /// <summary>Which node is selected and whether a server is expanded stay with the server through a reorder and a change of everything else about it. A new server is expanded; selecting a page of a collapsed server expands it.</summary>
   [Fact]
   public async Task Tree_SelectionAndExpansion_FollowTheServer()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(2));
      Assert.True(fixture.Shell.Select(jobs));
      ServerNode one = fixture.Shell.Tree.Servers[0];
      ServerNode two = fixture.Shell.Tree.Servers[1];
      one.IsExpanded = false;

      await fixture.Tick(Snapshots.Server(3), Snapshots.Server(2, "Renamed", health: ServerSnapshot.Red), Snapshots.Server(1));

      Assert.Equal(jobs, fixture.Shell.Selected);
      Assert.Same(two, fixture.Shell.Tree.Servers[1]);
      Assert.True(two.Children[1].IsSelected);
      Assert.Single(Selected(fixture.Shell.Tree));
      Assert.False(one.IsExpanded);
      Assert.True(two.IsExpanded);
      Assert.True(fixture.Shell.Tree.Servers[0].IsExpanded);
      Assert.Equal("Renamed", two.Name);
      Assert.Equal("Server.1" + Pages + " Server.2" + Pages + " Server.3" + Rest, fixture.TreeIds);

      Assert.True(fixture.Shell.Select(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1))));
      Assert.True(one.IsExpanded);
      Assert.True(one.Children[2].IsSelected);
      Assert.False(two.Children[1].IsSelected);
      Assert.Single(Selected(fixture.Shell.Tree));
   }



   /// <summary>
   /// When the server whose page is on screen is no longer in the snapshot, the selection moves to its neighbour's
   /// overview: the server that came after it, or the one before it when it was last. The page says that the
   /// server went, the log says so by identifier, and exactly one node is selected throughout.
   /// </summary>
   [Fact]
   public async Task Tree_SelectedServerRemoved_SelectionMovesToItsNeighbour()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2, "Bench"), Snapshots.Server(3));
      fixture.Shell.Select(PageKey.ForServer(PageKind.Connection, Snapshots.Id(2)));
      List<string?> raised = [];
      fixture.Shell.PropertyChanged += (_, change) => raised.Add(change.PropertyName);

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(3));

      Assert.Equal(PageKey.Server(Snapshots.Id(3)), fixture.Shell.Selected);
      Assert.True(fixture.Shell.Tree.Servers[1].IsSelected);
      Assert.Single(Selected(fixture.Shell.Tree));
      Assert.Equal(new FeedbackMessage("Server removed", "Bench is no longer on this computer.", FeedbackSeverity.Informational), fixture.Shell.Messages.Current);
      Assert.Equal("Server 3", fixture.Shell.CurrentPage.Title);
      Assert.Contains(nameof(ShellViewModel.Selected), raised, StringComparer.Ordinal);
      Assert.Contains($"INFO Server {Snapshots.Id(2)} is no longer on this computer; its page was on screen, and Server {Snapshots.Id(3)} is shown instead.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("Bench", StringComparison.Ordinal));

      await fixture.Tick(Snapshots.Server(1));

      Assert.Equal(PageKey.Server(Snapshots.Id(1)), fixture.Shell.Selected);
      Assert.Equal("Server 3 is no longer on this computer.", fixture.Shell.Messages.Current!.Message);
      Assert.Single(Selected(fixture.Shell.Tree));
      Assert.Empty(fixture.Wizard.Requests);
   }



   /// <summary>Several servers going at once: the selection moves to the nearest server after the one that was selected that is still there, and to the nearest before it when none after it is.</summary>
   [Fact]
   public async Task Tree_SelectedServerRemoved_WithOthers_TheNearestSurvivorIsSelected()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.WithServers(5));
      fixture.Shell.Select(PageKey.Server(Snapshots.Id(2)));

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(4), Snapshots.Server(5));
      Assert.Equal(PageKey.Server(Snapshots.Id(4)), fixture.Shell.Selected);

      fixture.Shell.Select(PageKey.Server(Snapshots.Id(5)));
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(4));
      Assert.Equal(PageKey.Server(Snapshots.Id(4)), fixture.Shell.Selected);
   }



   /// <summary>When the last server goes while its page is on screen and the stored data is ready, the window shows the wizard, as a computer with no server does, and asks for one; the wizard's page shows no message from elsewhere.</summary>
   [Fact]
   public async Task Tree_LastServerRemoved_ShowsTheWizard()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1));
      fixture.Shell.Select(PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1)));

      await fixture.Tick();

      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.True(fixture.Shell.Tree.AddServer.IsSelected);
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      Assert.True(fixture.Shell.WizardOpen);
      Assert.Null(fixture.Shell.Messages.Current);
   }



   /// <summary>
   /// Add server is disabled until the service has answered, enabled while there is room, and disabled with its
   /// reason at eight servers, of which one that is being removed is one. Asking to add a server then is refused out
   /// loud on the page on screen, and no wizard is asked for.
   /// </summary>
   [Fact]
   public async Task Tree_AddServer_DisabledWithItsReason_AtEight_CountingOneBeingRemoved()
   {
      using ShellFixture fixture = new();
      AddServerNode add = fixture.Shell.Tree.AddServer;
      Assert.False(add.IsEnabled);
      Assert.Equal("Checking the local print service…", add.DisabledReason);

      await fixture.Tick(Snapshots.WithServers(7));
      Assert.True(add.IsEnabled);
      Assert.Null(add.DisabledReason);

      ServerSnapshot[] eight = [.. Enumerable.Range(1, 7).Select(number => Snapshots.Server(number)), Snapshots.Server(8, removing: true)];
      await fixture.Tick(eight);

      Assert.Equal(8, ManagementProtocol.MaximumServers);
      Assert.False(add.IsEnabled);
      Assert.Equal("This computer already has 8 servers, the most it can have. A server that is being removed counts until its removal has finished. Remove a server to add another.", add.DisabledReason);
      PageKey before = fixture.Shell.Selected;
      Assert.False(fixture.Shell.StartAddServer());
      Assert.False(fixture.Shell.Select(PageKey.Wizard));
      Assert.Equal(before, fixture.Shell.Selected);
      Assert.Equal(new FeedbackMessage("Cannot start now", add.DisabledReason!, FeedbackSeverity.Warning), fixture.Shell.Messages.Current);
      Assert.Empty(fixture.Wizard.Requests);
      Assert.False(fixture.Shell.WizardOpen);
      Assert.Contains("INFO The wizard was asked for (add server) and did not start, because it cannot start now.", fixture.Log.Entries, StringComparer.Ordinal);

      await fixture.Tick(Snapshots.WithServers(7));
      Assert.True(add.IsEnabled);
      Assert.True(fixture.Shell.StartAddServer());
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
   }



   /// <summary>A server shows the marker for as long as its certificate exception is on, in words and in the name a screen reader says, and not afterwards.</summary>
   [Fact]
   public async Task Tree_ExceptionMarker_WhileTheExceptionIsOn()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(Snapshots.Server(1, "Bench", exception: true), Snapshots.Server(2));

      ServerNode bench = fixture.Shell.Tree.Servers[0];
      Assert.Equal("Certificate exception on", bench.ExceptionMarker);
      Assert.Equal("Bench, Ready, certificate exception on", bench.AccessibleName);
      Assert.Null(fixture.Shell.Tree.Servers[1].ExceptionMarker);
      Assert.Equal("Server 2, Ready", fixture.Shell.Tree.Servers[1].AccessibleName);

      await fixture.Tick(Snapshots.Server(1, "Bench"), Snapshots.Server(2));

      Assert.Null(bench.ExceptionMarker);
      Assert.Equal("Bench, Ready", bench.AccessibleName);
   }



   /// <summary>A server that is being removed is still in the tree, says so in its sentence and in its name, offers no command, and cannot be given a wizard.</summary>
   [Fact]
   public async Task Tree_ServerBeingRemoved_SaysSo_OffersNoCommand()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Bench"), Snapshots.Server(2));
      ServerNode bench = fixture.Shell.Tree.Servers[0];
      Assert.False(bench.IsRemoving);
      Assert.Equal(6, bench.Commands.All.Count);

      await fixture.Tick(Snapshots.Server(1, "Bench", removing: true, exception: true), Snapshots.Server(2));

      Assert.Same(bench, fixture.Shell.Tree.Servers[0]);
      Assert.True(bench.IsRemoving);
      Assert.Equal("Being removed", bench.StatusText);
      Assert.Equal("Bench, Ready, certificate exception on, being removed", bench.AccessibleName);
      Assert.Empty(bench.Commands.All);
      Assert.Same(ServerCommands.None, bench.Commands);
      Assert.Null(bench.Commands.Find(ServerCommandKind.Remove));
      Assert.Equal(6, fixture.Shell.Tree.Servers[1].Commands.All.Count);

      Assert.False(fixture.Shell.StartRepair(Snapshots.Id(1)));
      Assert.False(fixture.Shell.StartPairing(Snapshots.Id(1)));
      Assert.Equal("This server is being removed from this computer, so nothing about it can be changed.", fixture.Shell.Messages.Current!.Message);
      Assert.Empty(fixture.Wizard.Requests);
   }



   /// <summary>A health word this build does not know is shown as unknown, with its own glyph and words, and is never taken for healthy or for a failure.</summary>
   [Fact]
   public async Task Tree_UnknownHealth_IsUnknown()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(Snapshots.Server(1, health: "PURPLE"), Snapshots.Server(2));

      ServerNode purple = fixture.Shell.Tree.Servers[0];
      Assert.Equal(new HealthBadge(HealthLevel.Unknown, "Unknown", "Unknown"), purple.Health);
      Assert.Equal("Unknown", purple.GlyphName);
      Assert.Equal("State not known to this version · update the companion", purple.StatusText);
      Assert.Equal("Server 1, Unknown", purple.AccessibleName);
      Assert.Equal(HealthLevel.Green, fixture.Shell.Tree.Servers[1].Health.Level);
      Assert.Equal(HealthLevel.Unknown, fixture.Shell.Footer.Health.Level);
      Assert.Equal(ServiceAvailabilityKind.Reached, fixture.Shell.Availability.Kind);
   }



   /// <summary>When the service stops answering, what the last snapshot said about each server may no longer be true: every server is shown as unknown and says why, and is shown as it is again when the service answers.</summary>
   [Fact]
   public async Task Tree_ServiceStopsAnswering_EveryServerIsUnknown_UntilItAnswersAgain()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Red));

      await fixture.TickFailing(new IOException("The pipe is broken."));

      Assert.All(fixture.Shell.Tree.Servers, server =>
      {
         Assert.Equal(HealthLevel.Unknown, server.Health.Level);
         Assert.Equal("Not known while the local print service is not answering", server.StatusText);
      });
      Assert.Equal("Server.1" + Pages + " Server.2" + Pages + Rest, fixture.TreeIds);

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Red));

      Assert.Equal([HealthLevel.Green, HealthLevel.Red], fixture.Shell.Tree.Servers.Select(server => server.Health.Level));
      Assert.Equal("Connected and ready", fixture.Shell.Tree.Servers[0].StatusText);
   }



   /// <summary>
   /// A server's name and the service's explanation of its state are text the wire carried: both are shown in the
   /// contract's printable form, so a character that reorders or hides text never reaches the screen, and a name is
   /// held to the name rule's length. A name with nothing to show still names the node. The attention count is
   /// never shown negative.
   /// </summary>
   [Fact]
   public async Task Tree_NameAndServiceText_ArePrintable_AndBounded()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(Snapshots.Server(1, "Ware\u202Ehouse\u0007", status: "Paused\u2028by \u200Bthe admin.", attention: 3),
         Snapshots.Server(2, new string('n', 70) + "😀", status: new string('s', 600), attention: -2), Snapshots.Server(3, " \u200B "));

      ServerNode first = fixture.Shell.Tree.Servers[0];
      Assert.Equal("Ware house", first.Name);
      Assert.Equal("Paused by  the admin.", first.Detail);
      Assert.Equal(3, first.AttentionCount);
      ServerNode second = fixture.Shell.Tree.Servers[1];
      Assert.Equal(new string('n', 64), second.Name);
      Assert.Equal(ManagementProtocol.MaximumMessageCharacters, second.Detail.Length);
      Assert.Equal(0, second.AttentionCount);
      ServerNode third = fixture.Shell.Tree.Servers[2];
      Assert.Equal("Unnamed server", third.Name);
      Assert.Equal("Unnamed server, Ready", third.AccessibleName);
      Assert.Equal("Printers for Unnamed server", third.Children[0].AccessibleName);
      Assert.Equal("Unnamed server", ShellMessages.Name(null));
      Assert.Equal("Dock 2", ShellMessages.Name("  Dock 2 "));
      Assert.Equal(new string('n', 63), ShellMessages.Name(new string('n', 63) + "😀"));
   }



   /// <summary>A snapshot entry whose identifier is not a server identifier, or repeats another's, is left out and the log says so once for as long as it lasts; the other servers are shown.</summary>
   [Fact]
   public async Task Tree_EntriesThatAreNotServers_AreLeftOut_AndLoggedOnce()
   {
      using ShellFixture fixture = new();
      ServerSnapshot[] servers = [Snapshots.Server(1), Snapshots.Server(2) with { ServerId = "NOT-AN-IDENTIFIER" }, Snapshots.Server(3) with { ServerId = Snapshots.Id(1) }, Snapshots.Server(4)];

      await fixture.Tick(servers);
      await fixture.Tick(servers);

      Assert.Equal("Server 1,Server 4", fixture.ServerNames);
      Assert.Equal(["Server.1", "Server.2"], fixture.Shell.Tree.Servers.Select(node => node.AutomationId), StringComparer.Ordinal);
      Assert.Single(fixture.Log.Entries, entry => string.Equals(entry, "WARN The snapshot lists 2 servers this window cannot show: their identifier is not a server identifier, or another server has the same one.", StringComparison.Ordinal));

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(4));
      await fixture.Tick(servers);

      Assert.Equal(2, fixture.Log.Entries.Count(entry => entry.StartsWith("WARN The snapshot lists", StringComparison.Ordinal)));
   }



   /// <summary>A server's commands follow the snapshots, and the node says so only when they changed.</summary>
   [Fact]
   public async Task Commands_FollowTheSnapshot_AndAreRaisedOnlyWhenTheyChange()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      ServerNode first = fixture.Shell.Tree.Servers[0];
      List<string?> raised = [];
      first.PropertyChanged += (_, change) => raised.Add(change.PropertyName);

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      Assert.Empty(raised);

      await fixture.Tick(Snapshots.Server(2), Snapshots.Server(1));
      Assert.Equal([nameof(ServerNode.AutomationId), nameof(ServerNode.Commands), nameof(ServerNode.PositionInSet)], raised);
      Assert.True(first.Commands.Find(ServerCommandKind.MoveUp)!.IsEnabled);
      Assert.False(first.Commands.Find(ServerCommandKind.MoveDown)!.IsEnabled);
   }



   /// <summary>Counts the selected nodes of the tree, a server's pages included, and returns them.</summary>
   private static List<TreeNode> Selected(ServerTree tree)
      => [.. tree.Nodes.Concat(tree.Servers.SelectMany(server => server.Children)).Where(node => node.IsSelected)];
}
