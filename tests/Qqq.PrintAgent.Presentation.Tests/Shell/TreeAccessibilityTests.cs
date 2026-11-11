/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>What the tree gives assistive technology (ADR-0005, "Accessibility"): every node has a name, a glyph and a text; level, position and set size are right after every reconcile; and no state is told by colour alone.</summary>
public sealed class TreeAccessibilityTests
{
   /// <summary>Every node of the tree, a server's pages after it.</summary>
   private static List<TreeNode> All(ServerTree tree) => [.. tree.Nodes.SelectMany(node => node is ServerNode server ? [server, .. server.Children] : new[] { node })];



   /// <summary>Every node, in every state a server can be in and before anything was read, has an identifier, a name for a screen reader, the name of a glyph and a label, none of them empty.</summary>
   [Fact]
   public async Task Tree_EveryNode_HasANameAGlyphAndAText()
   {
      using ShellFixture fixture = new();
      Assert.All(All(fixture.Shell.Tree), HasEverything);

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Orange), Snapshots.Server(3, health: ServerSnapshot.Red, exception: true),
         Snapshots.Server(4, health: "PURPLE"), Snapshots.Server(5, removing: true), Snapshots.Server(6, "\u200B"), Snapshots.Server(7, paired: false), Snapshots.Server(8, stateUnreadable: true));
      Assert.Equal(8 * 4 + 4, All(fixture.Shell.Tree).Count);
      Assert.All(All(fixture.Shell.Tree), HasEverything);

      await fixture.TickFailing(new IOException("The pipe is broken."));
      Assert.All(All(fixture.Shell.Tree), HasEverything);

      Assert.Equal(["Server 1, Ready", "Server 2, Not ready", "Server 3, Needs attention, certificate exception on", "Server 4, Unknown"],
         (await Names(Snapshots.Server(1), Snapshots.Server(2, health: ServerSnapshot.Orange), Snapshots.Server(3, health: ServerSnapshot.Red, exception: true), Snapshots.Server(4, health: "PURPLE"))).Take(4), StringComparer.Ordinal);
      Assert.Equal(["Add server", "Computer", "Logs", "Settings"], fixture.Shell.Tree.Nodes.TakeLast(4).Select(node => node.AccessibleName), StringComparer.Ordinal);
      Assert.Equal(["AddServer", "Computer", "Logs", "Settings"], fixture.Shell.Tree.Nodes.TakeLast(4).Select(node => node.GlyphName), StringComparer.Ordinal);
      Assert.Equal(["Printers for Server 1", "Jobs for Server 1", "Connection for Server 1"], fixture.Shell.Tree.Servers[0].Children.Select(page => page.AccessibleName), StringComparer.Ordinal);
      Assert.Equal(["Printers", "Jobs", "Connection"], fixture.Shell.Tree.Servers[0].Children.Select(page => page.GlyphName), StringComparer.Ordinal);
      PageKind[] labelled = [PageKind.Printers, PageKind.Jobs, PageKind.Connection, PageKind.Wizard, PageKind.Computer, PageKind.Logs, PageKind.Settings];
      Assert.Equal(["Printers", "Jobs", "Connection", "Add server", "Computer", "Logs", "Settings"], labelled.Select(ShellMessages.NodeText), StringComparer.Ordinal);
      Assert.Equal("kind", Assert.Throws<ArgumentOutOfRangeException>(() => ShellMessages.NodeText(PageKind.Server)).ParamName);
   }



   /// <summary>A server's pages are named after the server, and follow it when it is renamed.</summary>
   [Fact]
   public async Task Tree_AServersPages_AreNamedAfterIt_AndFollowARename()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      ServerNode server = fixture.Shell.Tree.Servers[0];

      await fixture.Tick(Snapshots.Server(1, "Warehouse east"));

      Assert.Equal("Warehouse east", server.Text);
      Assert.Equal("Warehouse east, Ready", server.AccessibleName);
      Assert.Equal(["Printers for Warehouse east", "Jobs for Warehouse east", "Connection for Warehouse east"], server.Children.Select(page => page.AccessibleName), StringComparer.Ordinal);
      Assert.Equal(["NavigatePrinters", "NavigateJobs", "NavigateConnection"], server.Children.Select(page => page.AutomationId), StringComparer.Ordinal);
   }



   /// <summary>A server's glyph is its health's, and follows the health from one snapshot to the next, as its words and its name do, so a change of state is a change of shape and of words and not only of colour.</summary>
   [Fact]
   public async Task Tree_AServersGlyphAndWords_FollowItsHealth()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      ServerNode server = fixture.Shell.Tree.Servers[0];
      Assert.Equal(("CheckMark", "Ready", "Warehouse, Ready"), (server.GlyphName, server.Health.Text, server.AccessibleName));

      await fixture.Tick(Snapshots.Server(1, "Warehouse", health: ServerSnapshot.Red));
      Assert.Equal(("Error", "Needs attention", "Warehouse, Needs attention"), (server.GlyphName, server.Health.Text, server.AccessibleName));

      await fixture.Tick(Snapshots.Server(1, "Warehouse", health: ServerSnapshot.Orange, transport: "CONNECTING"));
      Assert.Equal(("Warning", "Not ready", "Warehouse, Not ready"), (server.GlyphName, server.Health.Text, server.AccessibleName));

      await fixture.Tick(Snapshots.Server(1, "Warehouse", health: "PURPLE"));
      Assert.Equal(("Unknown", "Unknown", "Warehouse, Unknown"), (server.GlyphName, server.Health.Text, server.AccessibleName));
   }



   /// <summary>After every reconcile each node knows its level, its position among its siblings counted from one, and how many siblings there are: the tree's own nodes are level 1, a server's three pages are level 2.</summary>
   [Fact]
   public async Task Tree_LevelPositionAndSetSize_AreRightAfterEveryReconcile()
   {
      using ShellFixture fixture = new();
      Check(fixture.Shell.Tree, servers: 0);

      await fixture.Tick(Snapshots.WithServers(3));
      Check(fixture.Shell.Tree, servers: 3);

      await fixture.Tick(Snapshots.Server(3), Snapshots.Server(1));
      Check(fixture.Shell.Tree, servers: 2);

      await fixture.Tick(Snapshots.WithServers(8));
      Check(fixture.Shell.Tree, servers: 8);

      await fixture.Tick();
      Check(fixture.Shell.Tree, servers: 0);
   }



   /// <summary>The Add server node says in its name that a wizard is in progress, and stays enabled while one is open even when no new one could start.</summary>
   [Fact]
   public async Task AddServer_WhileAWizardIsOpen_SaysSo_AndStaysEnabled()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.WithServers(7));
      AddServerNode add = fixture.Shell.Tree.AddServer;
      Assert.False(add.InProgress);

      fixture.Shell.StartAddServer();

      Assert.True(add.InProgress);
      Assert.Equal("Add server, in progress", add.AccessibleName);
      Assert.Equal("Add server", add.Text);

      await fixture.Tick(Snapshots.WithServers(8));

      Assert.True(add.IsEnabled);
      Assert.Null(add.DisabledReason);
      Assert.True(add.InProgress);

      fixture.Shell.WizardEnded();

      Assert.False(add.InProgress);
      Assert.False(add.IsEnabled);
      Assert.Equal(ShellMessages.ServerLimit, add.DisabledReason);
      Assert.Equal("Add server", add.AccessibleName);
   }



   /// <summary>One tick with these servers, and the names a screen reader says for the tree's nodes afterwards.</summary>
   private static async Task<List<string>> Names(params ServerSnapshot[] servers)
   {
      using ShellFixture fixture = new();
      await fixture.Tick(servers);
      return [.. fixture.Shell.Tree.Nodes.Select(node => node.AccessibleName)];
   }



   /// <summary>A node has an identifier, a name, a glyph and a label.</summary>
   private static void HasEverything(TreeNode node)
   {
      Assert.False(string.IsNullOrWhiteSpace(node.AutomationId));
      Assert.False(string.IsNullOrWhiteSpace(node.AccessibleName));
      Assert.False(string.IsNullOrWhiteSpace(node.GlyphName));
      Assert.False(string.IsNullOrWhiteSpace(node.Text));
   }



   /// <summary>The tree's own nodes are level 1 and numbered one to their count; each server's pages are level 2 and numbered one to three.</summary>
   private static void Check(ServerTree tree, int servers)
   {
      Assert.Equal(servers + 4, tree.Nodes.Count);
      Assert.Equal(servers, tree.Servers.Count);
      for(int index = 0; index < tree.Nodes.Count; index++)
      {
         Assert.Equal((1, index + 1, servers + 4), (tree.Nodes[index].Level, tree.Nodes[index].PositionInSet, tree.Nodes[index].SizeOfSet));
      }
      foreach(ServerNode server in tree.Servers)
      {
         Assert.Equal([(2, 1, 3), (2, 2, 3), (2, 3, 3)], server.Children.Select(page => (page.Level, page.PositionInSet, page.SizeOfSet)));
      }
   }
}
