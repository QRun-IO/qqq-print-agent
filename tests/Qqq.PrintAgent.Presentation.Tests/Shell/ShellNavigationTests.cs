/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>Which page is on screen: where the window lands, what the operator chooses, and how the shell asks for the wizard and is told that it ended, without knowing the wizard.</summary>
public sealed class ShellNavigationTests
{
   /// <summary>After an upgrade from 1.x with one server the window lands on that server's Printers page, where 1.x opened, with the server expanded and no wizard; with several servers it lands on the first one's.</summary>
   [Fact]
   public async Task Landing_WithServers_IsTheFirstServersPrintersPage()
   {
      using ShellFixture fixture = new();
      List<string?> raised = [];
      fixture.Shell.PropertyChanged += (_, change) => raised.Add(change.PropertyName);

      await fixture.Tick(Snapshots.Server(1, "print.example.com"), Snapshots.Server(2));

      var printers = PageKey.ForServer(PageKind.Printers, Snapshots.Id(1));
      Assert.Equal(printers, fixture.Shell.Selected);
      Assert.Equal(new PageHeading(printers, "Printers", "Choose which printers this computer shares with print.example.com."), fixture.Shell.CurrentPage);
      Assert.True(fixture.Shell.Tree.Servers[0].IsExpanded);
      Assert.True(fixture.Shell.Tree.Servers[0].Children[0].IsSelected);
      Assert.False(fixture.Shell.Tree.Computer.IsSelected);
      Assert.Empty(fixture.Wizard.Requests);
      Assert.False(fixture.Shell.WizardOpen);
      Assert.Contains(nameof(ShellViewModel.Selected), raised, StringComparer.Ordinal);
      Assert.Contains(nameof(ShellViewModel.CurrentPage), raised, StringComparer.Ordinal);
      Assert.Contains(nameof(ShellViewModel.Snapshot), raised, StringComparer.Ordinal);
      Assert.Contains(nameof(ShellViewModel.Availability), raised, StringComparer.Ordinal);
      Assert.Contains(nameof(ShellViewModel.Footer), raised, StringComparer.Ordinal);
      Assert.Contains(nameof(ShellViewModel.Tray), raised, StringComparer.Ordinal);
   }



   /// <summary>
   /// The window lands once. A later snapshot does not move the operator, and neither does the first answer when
   /// they had already chosen a page while the service was not answering. A page the owner of the wizard had the
   /// shell open counts as chosen in the same way, whenever it was opened: the first answer does not replace it.
   /// </summary>
   [Fact]
   public async Task Landing_HappensOnce_AndNeverAfterTheOperatorChose()
   {
      using ShellFixture told = new();
      told.Shell.WizardEnded(PageKey.Logs);
      await told.Tick(Snapshots.Server(1));
      Assert.Equal(PageKey.Logs, told.Shell.Selected);

      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      Assert.True(fixture.Shell.Select(PageKey.Logs));

      await fixture.Tick(Snapshots.Server(2), Snapshots.Server(1));
      Assert.Equal(PageKey.Logs, fixture.Shell.Selected);

      using ShellFixture early = new();
      await early.TickFailing(new IOException("The pipe is broken."));
      Assert.True(early.Shell.Select(PageKey.Settings));
      await early.Tick();

      Assert.Equal(PageKey.Settings, early.Shell.Selected);
      Assert.Empty(early.Wizard.Requests);
      Assert.Equal(new PageHeading(PageKey.Settings, "Settings", "Appearance, updates and application information."), early.Shell.CurrentPage);
   }



   /// <summary>A computer whose stored data was not ready lands when it is: the window stays on the Computer page while the upgrade is incomplete, and shows the wizard once the service answers ready with no server.</summary>
   [Fact]
   public async Task Landing_WaitsUntilTheStoredDataIsReady()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(new ServiceSnapshot(Snapshots.Computer(new StoredState(StoredState.UpgradeIncomplete, "connection.dpapi")), []));
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);

      await fixture.Tick();

      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
   }



   /// <summary>The operator chooses a page: it is selected, its heading is worded with the server's name, and a page of a server that is not in the tree is not shown.</summary>
   [Fact]
   public async Task Select_ShowsThePage_WithItsHeading()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      string id = Snapshots.Id(1);

      Assert.True(fixture.Shell.Select(PageKey.Server(id)));
      Assert.Equal(new PageHeading(PageKey.Server(id), "Warehouse", "The state of this server and what needs doing."), fixture.Shell.CurrentPage);
      Assert.True(fixture.Shell.Select(PageKey.ForServer(PageKind.Jobs, id)));
      Assert.Equal(new PageHeading(PageKey.ForServer(PageKind.Jobs, id), "Jobs", "Recent jobs this computer received from Warehouse."), fixture.Shell.CurrentPage);
      Assert.True(fixture.Shell.Select(PageKey.ForServer(PageKind.Connection, id)));
      Assert.Equal(new PageHeading(PageKey.ForServer(PageKind.Connection, id), "Connection", "Sign-in, pairing and the connection to Warehouse."), fixture.Shell.CurrentPage);
      Assert.True(fixture.Shell.Select(PageKey.Logs));
      Assert.Equal(new PageHeading(PageKey.Logs, "Logs", "Activity and diagnostics in the AppKit log viewer."), fixture.Shell.CurrentPage);
      Assert.True(fixture.Shell.Select(PageKey.Computer));
      Assert.True(fixture.Shell.Tree.Computer.IsSelected);

      Assert.False(fixture.Shell.Select(PageKey.ForServer(PageKind.Jobs, Snapshots.Id(9))));
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Contains($"DEBUG Page Jobs {Snapshots.Id(9)} was asked for and is not in the tree.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Throws<ArgumentNullException>(() => fixture.Shell.Select(null!));
      Assert.Equal("Unnamed server", ShellMessages.Heading(PageKey.Server(id), null).Title);
      Assert.Throws<ArgumentNullException>(() => ShellMessages.Heading(null!, "Warehouse"));
   }



   /// <summary>Choosing the page that is already on screen changes nothing: its message stays, nothing is announced to the bindings, and the wizard is not asked for again. It still counts as the operator's choice, so the window does not land elsewhere afterwards.</summary>
   [Fact]
   public async Task Select_ThePageOnScreen_ChangesNothing()
   {
      using ShellFixture fixture = new();
      Assert.True(fixture.Shell.Select(PageKey.Computer));
      await fixture.Tick(Snapshots.Server(1));
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);

      fixture.Shell.StartAddServer();
      fixture.Shell.Messages.Show(PageKey.Wizard, new FeedbackMessage("Check", "The server passed every check.", FeedbackSeverity.Success));
      List<string?> raised = [];
      fixture.Shell.PropertyChanged += (_, change) => raised.Add(change.PropertyName);

      Assert.True(fixture.Shell.Select(PageKey.Wizard));

      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      Assert.Equal("The server passed every check.", fixture.Shell.Messages.Current!.Message);
      Assert.Empty(raised);
   }



   /// <summary>A page's heading follows the server's name when a snapshot renames it.</summary>
   [Fact]
   public async Task Heading_FollowsARename()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"));

      await fixture.Tick(Snapshots.Server(1, "Warehouse east"));

      Assert.Equal("Choose which printers this computer shares with Warehouse east.", fixture.Shell.CurrentPage.Description);
   }



   /// <summary>
   /// Asking to add a server shows the wizard's page and then asks for the wizard. Going back to the wizard's page
   /// through the Add server node while that wizard is open only shows the page: the launcher is not asked again.
   /// Asking for the wizard that adds a server in so many words is a new request, which the launcher answers.
   /// </summary>
   [Fact]
   public async Task StartAddServer_ShowsTheWizardsPage_ThenAsksForTheWizard()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1));
      fixture.Shell.Messages.Show(fixture.Shell.Selected, new FeedbackMessage("Printers", "Something about printers.", FeedbackSeverity.Informational));

      Assert.True(fixture.Shell.StartAddServer());

      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      Assert.True(fixture.Shell.WizardOpen);
      Assert.Null(fixture.Shell.Messages.Current);
      Assert.True(fixture.Shell.Tree.AddServer.IsSelected);

      fixture.Shell.Select(PageKey.Logs);
      Assert.True(fixture.Shell.Select(PageKey.Wizard));

      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);

      fixture.Shell.Select(PageKey.Logs);
      Assert.True(fixture.Shell.StartAddServer());

      Assert.Equal(["add on Wizard", "add on Wizard"], fixture.Wizard.Requests);
      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
   }



   /// <summary>
   /// A repair wizard is open and the operator is on a server's Jobs page. Choosing the Add server node, which says
   /// "in progress", takes them back to the wizard's page and asks the launcher for nothing: the launcher was called
   /// once in all, for the repair, so the wizard in progress is not restarted or replaced. A server the wizard
   /// asked the shell to show later is not opened over the operator's choice.
   /// </summary>
   [Fact]
   public async Task Select_TheAddServerNode_WhileAWizardIsOpen_OnlyShowsItsPage()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      fixture.Shell.StartRepair(Snapshots.Id(1));
      fixture.Shell.Select(PageKey.ForServer(PageKind.Jobs, Snapshots.Id(2)));

      Assert.True(fixture.Shell.Select(PageKey.Wizard));

      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.Equal([$"repair {Snapshots.Id(1)} on Wizard"], fixture.Wizard.Requests);
      Assert.True(fixture.Shell.WizardOpen);
      Assert.True(fixture.Shell.Tree.AddServer.IsSelected);
   }



   /// <summary>
   /// A view may pass every selection of its tree on to the shell, including the one that follows the shell's own
   /// move. A wizard is recorded as open before its page is selected, so the selection that is passed on while the
   /// page is being shown asks for nothing: each wizard is asked for once, when the window lands on it and when the
   /// operator asks for it.
   /// </summary>
   [Fact]
   public async Task Select_PassedOnByAViewForEveryMove_AsksForEachWizardOnce()
   {
      using ShellFixture fixture = new();
      int depth = 0;
      fixture.Shell.PropertyChanged += (_, change) =>
      {
         if(string.Equals(change.PropertyName, nameof(ShellViewModel.Selected), StringComparison.Ordinal))
         {
            Assert.True(++depth < 8, "Passing a selection on led to the same selection again, without end.");
            fixture.Shell.Select(fixture.Shell.Selected);
            depth--;
         }
      };

      await fixture.Tick();

      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);

      await fixture.Tick(Snapshots.Server(1));
      fixture.Shell.WizardEnded(PageKey.Server(Snapshots.Id(1)));
      fixture.Shell.StartAddServer();

      Assert.Equal(["add on Wizard", "add on Wizard"], fixture.Wizard.Requests);
      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
   }



   /// <summary>While a wizard is open and no new one can start, asking to add a server takes the operator back to the one in progress and asks for nothing.</summary>
   [Fact]
   public async Task StartAddServer_WhenNoneCanStartAndOneIsOpen_GoesBackToIt()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.WithServers(7));
      fixture.Shell.StartAddServer();
      await fixture.Tick(Snapshots.WithServers(8));
      fixture.Shell.Select(PageKey.Server(Snapshots.Id(8)));

      Assert.True(fixture.Shell.StartAddServer());

      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      Assert.Null(fixture.Shell.Messages.Current);
   }



   /// <summary>A repair and a pairing each show the wizard's page and then ask for the wizard with the server's identifier; for a server that is not on this computer, or while the service is not available, they are refused out loud and nothing is asked for.</summary>
   [Fact]
   public async Task StartRepairAndPairing_AskForTheWizard_OrAreRefusedOutLoud()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      string two = Snapshots.Id(2);

      Assert.True(fixture.Shell.StartRepair(two));
      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.True(fixture.Shell.WizardOpen);
      fixture.Shell.Select(PageKey.Server(two));
      Assert.True(fixture.Shell.StartPairing(two));
      Assert.Equal([$"repair {two} on Wizard", $"pair {two} on Wizard"], fixture.Wizard.Requests);

      fixture.Shell.Select(PageKey.Logs);
      Assert.False(fixture.Shell.StartRepair(Snapshots.Id(9)));
      Assert.Equal(new FeedbackMessage("Cannot start now", "That server is not in this window's list. It may have been removed from this computer.", FeedbackSeverity.Warning), fixture.Shell.Messages.Current);
      Assert.False(fixture.Shell.StartPairing(null!));
      Assert.Equal(PageKey.Logs, fixture.Shell.Selected);

      await fixture.TickFailing(new IOException("The pipe is broken."));
      Assert.False(fixture.Shell.StartRepair(two));
      Assert.False(fixture.Shell.StartPairing(two));
      Assert.Equal("The local print service could not be reached.", fixture.Shell.Messages.Current!.Message);
      Assert.Equal(2, fixture.Wizard.Requests.Count);
      Assert.Equal(["repair", "pairing", "repair", "pairing"],
         fixture.Log.Entries.Where(entry => entry.StartsWith("INFO The wizard was asked for", StringComparison.Ordinal)).Select(entry => entry.Split('(', ')')[1]), StringComparer.Ordinal);
   }



   /// <summary>
   /// The limit of eight servers is a limit on adding one. At eight servers a repair and a pairing of a server that
   /// exists still start, each with one request to the launcher, so an operator at the limit can sign in again;
   /// only the wizard that adds a server is refused, with the limit as the reason.
   /// </summary>
   [Fact]
   public async Task StartRepairAndPairing_AtEightServers_AreNotHeldToTheLimitOnAdding()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.WithServers(8));
      Assert.False(fixture.Shell.Tree.AddServer.IsEnabled);

      Assert.True(fixture.Shell.StartRepair(Snapshots.Id(3)));
      fixture.Shell.WizardEnded(PageKey.Server(Snapshots.Id(3)));
      Assert.True(fixture.Shell.StartPairing(Snapshots.Id(4)));

      Assert.Equal([$"repair {Snapshots.Id(3)} on Wizard", $"pair {Snapshots.Id(4)} on Wizard"], fixture.Wizard.Requests);
      Assert.Null(fixture.Shell.Messages.Current);

      fixture.Shell.WizardEnded();
      Assert.False(fixture.Shell.StartAddServer());

      Assert.Equal(new FeedbackMessage("Cannot start now", ShellMessages.ServerLimit, FeedbackSeverity.Warning), fixture.Shell.Messages.Current);
      Assert.Equal(2, fixture.Wizard.Requests.Count);
   }



   /// <summary>When the last server goes while a wizard is open, the wizard's page is shown, as for a computer with no server, and the wizard that is open stays the only one: no second one is asked for.</summary>
   [Fact]
   public async Task LastServerGoes_WhileAWizardIsOpen_NoSecondWizardIsAskedFor()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1));
      fixture.Shell.StartRepair(Snapshots.Id(1));
      fixture.Shell.Select(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1)));

      await fixture.Tick();

      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.Equal([$"repair {Snapshots.Id(1)} on Wizard"], fixture.Wizard.Requests);
      Assert.True(fixture.Shell.WizardOpen);
   }



   /// <summary>When the wizard has ended with a page to open, such as the server it added, that page is shown and the Add server node no longer says that a wizard is in progress.</summary>
   [Fact]
   public async Task WizardEnded_OpensThePageItNames()
   {
      using ShellFixture fixture = new();
      await fixture.Tick();
      Assert.True(fixture.Shell.WizardOpen);
      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);

      fixture.Shell.WizardEnded(PageKey.Server(Snapshots.Id(1)));

      Assert.False(fixture.Shell.WizardOpen);
      Assert.False(fixture.Shell.Tree.AddServer.InProgress);
      Assert.Equal(PageKey.Server(Snapshots.Id(1)), fixture.Shell.Selected);
      Assert.Equal("Warehouse", fixture.Shell.CurrentPage.Title);
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
   }



   /// <summary>
   /// The wizard can end before a snapshot lists the server it added. The Computer page stands in, no new wizard is
   /// asked for although the tree has no server yet, and the server's page is shown as soon as a snapshot lists it;
   /// if the operator chooses a page meanwhile, their choice stands.
   /// </summary>
   [Fact]
   public async Task WizardEnded_WithAServerNotListedYet_ShowsItWhenItIs()
   {
      using ShellFixture fixture = new();
      await fixture.Tick();
      var added = PageKey.Server(Snapshots.Id(1));

      fixture.Shell.WizardEnded(added);

      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      await fixture.Tick(Snapshots.Server(1));
      Assert.Equal(added, fixture.Shell.Selected);
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      Assert.Equal(added, fixture.Shell.Selected);

      fixture.Shell.StartAddServer();
      fixture.Shell.Select(PageKey.Logs);
      fixture.Shell.WizardEnded(PageKey.Server(Snapshots.Id(3)));
      Assert.Equal(PageKey.Logs, fixture.Shell.Selected);
      fixture.Shell.Select(PageKey.Settings);
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2), Snapshots.Server(3));
      Assert.Equal(PageKey.Settings, fixture.Shell.Selected);
   }



   /// <summary>
   /// When the wizard ended without a page to open (it was cancelled) and its page is on screen, the window goes
   /// where it would land: the first server's Printers page; with no server, a new wizard, because a computer with
   /// no server shows the wizard; and the Computer page when the stored data is no longer ready. The new wizard is
   /// not asked for inside the call that says the old one has ended: the wizard's page stays, with no wizard open,
   /// and the next snapshot asks. When the operator is on another page, they stay there.
   /// </summary>
   [Fact]
   public async Task WizardEnded_WithNoPage_GoesWhereTheWindowWouldLand()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      fixture.Shell.StartAddServer();

      fixture.Shell.WizardEnded();
      Assert.Equal(PageKey.ForServer(PageKind.Printers, Snapshots.Id(1)), fixture.Shell.Selected);
      Assert.False(fixture.Shell.WizardOpen);

      fixture.Shell.StartRepair(Snapshots.Id(2));
      fixture.Shell.Select(PageKey.Logs);
      fixture.Shell.WizardEnded(PageKey.Wizard);
      Assert.Equal(PageKey.Logs, fixture.Shell.Selected);
      Assert.Equal(2, fixture.Wizard.Requests.Count);

      using ShellFixture empty = new();
      await empty.Tick();
      empty.Shell.WizardEnded();
      Assert.Equal(PageKey.Wizard, empty.Shell.Selected);
      Assert.Equal(["add on Wizard"], empty.Wizard.Requests);
      Assert.False(empty.Shell.WizardOpen);
      Assert.False(empty.Shell.Tree.AddServer.InProgress);
      Assert.True(empty.Shell.Tree.AddServer.IsEnabled);

      await empty.Tick();
      Assert.Equal(PageKey.Wizard, empty.Shell.Selected);
      Assert.Equal(["add on Wizard", "add on Wizard"], empty.Wizard.Requests);
      Assert.True(empty.Shell.WizardOpen);
      Assert.True(empty.Shell.Tree.AddServer.InProgress);

      await empty.Tick(new ServiceSnapshot(Snapshots.Computer(new StoredState(StoredState.NewerBuild, null, 4, 3)), []));
      empty.Shell.WizardEnded();
      Assert.Equal(PageKey.Computer, empty.Shell.Selected);
      Assert.False(empty.Shell.WizardOpen);
      Assert.Equal(2, empty.Wizard.Requests.Count);
   }



   /// <summary>
   /// A wizard that ends with nothing to show on a computer with no server leaves its page on screen with no wizard
   /// open, and what happens next is decided outside the call that ended it. The operator who chooses Add server
   /// gets a wizard at once. Otherwise the next snapshot decides: a server it lists (one the wizard left in place)
   /// is where the window goes, with no wizard; and when the service has stopped answering, the Computer page.
   /// </summary>
   [Fact]
   public async Task WizardEnded_WithNothingToShow_TheNextSnapshotOrTheOperatorDecides()
   {
      using ShellFixture chosen = new();
      await chosen.Tick();
      chosen.Shell.WizardEnded();
      Assert.True(chosen.Shell.Select(PageKey.Wizard));
      Assert.Equal(["add on Wizard", "add on Wizard"], chosen.Wizard.Requests);
      Assert.True(chosen.Shell.WizardOpen);
      await chosen.Tick();
      Assert.Equal(2, chosen.Wizard.Requests.Count);

      using ShellFixture kept = new();
      await kept.Tick();
      kept.Shell.WizardEnded();
      await kept.Tick(Snapshots.Server(1));
      Assert.Equal(PageKey.ForServer(PageKind.Printers, Snapshots.Id(1)), kept.Shell.Selected);
      Assert.Equal(["add on Wizard"], kept.Wizard.Requests);
      Assert.False(kept.Shell.WizardOpen);

      using ShellFixture stopped = new();
      await stopped.Tick();
      stopped.Shell.WizardEnded();
      await stopped.TickFailing(new IOException("The pipe is broken."));
      Assert.Equal(PageKey.Computer, stopped.Shell.Selected);
      Assert.Equal(["add on Wizard"], stopped.Wizard.Requests);
   }



   /// <summary>
   /// A launcher whose wizard ends at once, inside the call that asked for it, is not asked again from inside that
   /// call, however often it ends: each snapshot asks once. If the shell asked from inside
   /// <see cref="ShellViewModel.WizardEnded"/>, the two would call each other without end.
   /// </summary>
   [Fact]
   public async Task WizardEnded_InsideTheLaunchersOwnCall_IsNotAnsweredWithANewRequest()
   {
      ShellFixture fixture = new();
      EndsAtOnce launcher = new();
      using ShellViewModel shell = new(fixture.Client, fixture.Gate, launcher, fixture.Log, TestData.Time, ShellFixture.Product);
      launcher.Shell = shell;
      fixture.Dispose();

      fixture.Pipe.Answer(Replies.Success(Snapshots.Of()));
      await shell.RefreshAsync();

      Assert.Equal(1, launcher.Asked);
      Assert.Equal(PageKey.Wizard, shell.Selected);
      Assert.False(shell.WizardOpen);

      fixture.Pipe.Answer(Replies.Success(Snapshots.Of()));
      await shell.RefreshAsync();

      Assert.Equal(2, launcher.Asked);
   }



   /// <summary>A launcher whose wizard ends, with nothing to show, inside the call that asks for it. It refuses to be asked more often than a test can mean, so a shell that asks from inside the ending fails the test instead of the run.</summary>
   private sealed class EndsAtOnce : IWizardLauncher
   {
      /// <summary>The shell that is told that the wizard ended.</summary>
      internal ShellViewModel? Shell { get; set; }



      /// <summary>How often the wizard that adds a server was asked for.</summary>
      internal int Asked { get; private set; }



      /// <summary>Counts the request and ends the wizard at once.</summary>
      public void AddServer()
      {
         if(++Asked > 4)
         {
            throw new InvalidOperationException("The wizard was asked for again from inside the call that ended it.");
         }
         Shell!.WizardEnded();
      }



      /// <summary>Not used.</summary>
      public void Repair(string serverId) => throw new NotSupportedException();



      /// <summary>Not used.</summary>
      public void Pair(string serverId) => throw new NotSupportedException();
   }
}
