/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>What the window knows of the local service and which page it shows for it: the wizard only when the snapshot answered and the stored data is ready, and otherwise the Computer page, which says why.</summary>
public sealed class ServiceAvailabilityTests
{
   /// <summary>A snapshot of a computer with no server whose stored data is in this state.</summary>
   private static ServiceSnapshot NotReady(StoredState state) => new(Snapshots.Computer(state), []);



   /// <summary>Before the first read has ended the Computer page is on screen and says that the service is being checked; no wizard is asked for.</summary>
   [Fact]
   public void BeforeTheFirstRead_ShowsComputer_SaysItIsChecking()
   {
      using ShellFixture fixture = new();

      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.Checking, "Checking the local print service…", ""), fixture.Shell.Availability);
      Assert.False(fixture.Shell.Availability.Answered);
      Assert.False(fixture.Shell.Availability.StoredDataReady);
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Equal(new PageHeading(PageKey.Computer, "Computer", "This computer's details, the Windows service and companion startup."), fixture.Shell.CurrentPage);
      Assert.Null(fixture.Shell.Snapshot);
      Assert.Empty(fixture.Wizard.Requests);
      Assert.False(fixture.Shell.StartAddServer());
      Assert.Equal("Checking the local print service…", fixture.Shell.Messages.Current!.Message);
   }



   /// <summary>With no server, when the snapshot answered and the stored data is ready, the window shows the wizard and asks for the one that adds a server, once, after its page is on screen.</summary>
   [Fact]
   public async Task NoServers_StoredDataReady_ShowsTheWizard()
   {
      using ShellFixture fixture = new();

      await fixture.Tick();

      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.Reached, "The local print service is running.", ""), fixture.Shell.Availability);
      Assert.True(fixture.Shell.Availability.Answered && fixture.Shell.Availability.StoredDataReady);
      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.True(fixture.Shell.Tree.AddServer.IsSelected);
      Assert.Equal(new PageHeading(PageKey.Wizard, "Connect to a server", "Check a server, sign in and pair this computer."), fixture.Shell.CurrentPage);
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      Assert.True(fixture.Shell.WizardOpen);
      Assert.True(fixture.Shell.Tree.AddServer.IsEnabled);
      Assert.True(fixture.Shell.Tree.AddServer.InProgress);

      await fixture.Tick();
      await fixture.Tick();

      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
   }



   /// <summary>
   /// With no server because the upgrade from 1.x stopped, the Computer page stays on screen and names the file the
   /// upgrade stopped at, in printable form; no wizard is asked for, Add server is disabled with that reason, and
   /// asking for it is refused out loud. The service's own upgrade-incomplete golden reply reads the same way.
   /// </summary>
   [Fact]
   public async Task NoServers_UpgradeIncomplete_ShowsComputer_NamesTheFile()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(NotReady(new StoredState(StoredState.UpgradeIncomplete, "connection\u202E.dpapi")));

      Assert.Equal(ServiceAvailabilityKind.UpgradeIncomplete, fixture.Shell.Availability.Kind);
      Assert.Equal("The upgrade from an earlier version stopped at the file connection .dpapi, so the service is not printing.", fixture.Shell.Availability.Summary);
      Assert.Equal("The service tries the upgrade again each time it starts: restart the service from this page. If it stops again, see Logs for the reason. No server can be added until the upgrade has finished.",
         fixture.Shell.Availability.Guidance);
      Assert.True(fixture.Shell.Availability.Answered);
      Assert.False(fixture.Shell.Availability.StoredDataReady);
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Empty(fixture.Wizard.Requests);
      Assert.False(fixture.Shell.Tree.AddServer.IsEnabled);
      Assert.Equal(fixture.Shell.Availability.Summary, fixture.Shell.Tree.AddServer.DisabledReason);
      Assert.False(fixture.Shell.StartAddServer());
      Assert.Equal(new FeedbackMessage("Cannot start now", fixture.Shell.Availability.Summary, FeedbackSeverity.Warning), fixture.Shell.Messages.Current);
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Equal("Upgrade not finished · not printing", fixture.Shell.Footer.Text);
      Assert.Contains("WARN The local print service is not available to this window (UpgradeIncomplete).", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("dpapi", StringComparison.Ordinal));

      await fixture.Tick(NotReady(new StoredState(StoredState.UpgradeIncomplete)));
      Assert.Equal("The upgrade from an earlier version did not finish, so the service is not printing.", fixture.Shell.Availability.Summary);
      await fixture.Tick(NotReady(new StoredState(StoredState.UpgradeIncomplete, " \u0007 ")));
      Assert.Equal("The upgrade from an earlier version did not finish, so the service is not printing.", fixture.Shell.Availability.Summary);
      await fixture.Tick(NotReady(new StoredState(StoredState.UpgradeIncomplete, new string('f', 300))));
      Assert.Contains(" " + new string('f', 260) + ",", fixture.Shell.Availability.Summary, StringComparison.Ordinal);

      using ShellFixture golden = new();
      await golden.Tick(Replies.Golden("snapshot.reply.json"));
      Assert.Equal("The upgrade from an earlier version stopped at the file connection.dpapi, so the service is not printing.", golden.Shell.Availability.Summary);
      Assert.Empty(golden.Wizard.Requests);
   }



   /// <summary>With no server because a newer build wrote the stored data, the Computer page stays and names both versions: the one found and the newest this build reads. The service's own golden reply reads the same way.</summary>
   [Fact]
   public async Task NoServers_NewerBuild_ShowsComputer_NamesBothVersions()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(Replies.Golden("snapshot.degraded.reply.json"));

      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.NewerBuild,
         "A newer build of the print agent wrote the stored data (version 4; this build reads up to version 3), so the service is not printing.",
         "Install that newer build again; this build never changes data a newer one wrote. Updates are in Settings."), fixture.Shell.Availability);
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Empty(fixture.Shell.Tree.Servers);
      Assert.Empty(fixture.Wizard.Requests);
      Assert.False(fixture.Shell.Tree.AddServer.IsEnabled);
      Assert.Equal(fixture.Shell.Availability.Summary, fixture.Shell.Tree.AddServer.DisabledReason);
      Assert.Equal("Stored data is from a newer build · not printing", fixture.Shell.Footer.Text);

      await fixture.Tick(NotReady(new StoredState(StoredState.NewerBuild, FoundVersion: 4)));
      Assert.Equal("A newer build of the print agent wrote the stored data, so the service is not printing.", fixture.Shell.Availability.Summary);
      await fixture.Tick(NotReady(new StoredState(StoredState.NewerBuild, SupportedVersion: 3)));
      Assert.Equal("A newer build of the print agent wrote the stored data, so the service is not printing.", fixture.Shell.Availability.Summary);
   }



   /// <summary>When the server whose page is on screen is gone because the service now says that its stored data is not ready, the Computer page is shown, which says why, and no wizard is asked for.</summary>
   [Fact]
   public async Task SelectedServerGone_StoredDataNotReady_ShowsComputer_NotTheWizard()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1));
      fixture.Shell.Select(PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1)));

      await fixture.Tick(NotReady(new StoredState(StoredState.NewerBuild, null, 4, 3)));

      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.True(fixture.Shell.Tree.Computer.IsSelected);
      Assert.Empty(fixture.Wizard.Requests);
      Assert.False(fixture.Shell.WizardOpen);
      Assert.Equal("Server 1 is no longer on this computer.", fixture.Shell.Messages.Current!.Message);
   }



   /// <summary>A stored-data status this build does not know is not ready: the Computer page says so and says to update the companion, and no wizard is asked for.</summary>
   [Fact]
   public async Task NoServers_StatusThisBuildDoesNotKnow_IsNotReady()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(NotReady(new StoredState("repairing")));

      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.StoredDataNotReady,
         "The service reports that its stored data is not ready, in a way this version of the companion does not know, so the service is not printing.",
         "Update the companion to the service's version. Updates are in Settings."), fixture.Shell.Availability);
      Assert.True(fixture.Shell.Availability.Answered);
      Assert.False(fixture.Shell.Availability.StoredDataReady);
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Empty(fixture.Wizard.Requests);
   }



   /// <summary>
   /// A service that is not reached, one that is older and one that is newer are told apart by the typed result,
   /// and each has its own guidance: the service controls (led by what Windows says of the service, when that is
   /// known), finishing the update, and updating the companion. The Computer page stays on screen, no wizard is
   /// asked for, and Add server is disabled with the reason.
   /// </summary>
   [Fact]
   public async Task ServiceNotReached_OlderService_NewerService_EachHasItsGuidance()
   {
      using ShellFixture fixture = new();

      await fixture.TickFailing(new AgentPipeNotSentException(new IOException("Nothing is listening.")));

      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.NotReached, "The local print service could not be reached.",
         "Use the service controls on this page, Windows Services or the installer. This window needs administrator access. This window keeps trying. What it shows of the servers may be out of date until the service answers."),
         fixture.Shell.Availability);
      Assert.False(fixture.Shell.Availability.Answered);
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Equal("The local print service could not be reached.", fixture.Shell.Tree.AddServer.DisabledReason);

      fixture.Shell.ServiceControlGuidance = "  The Windows service is stopped. Start it.  ";
      await fixture.TickFailing(new IOException("The pipe is broken."));
      Assert.StartsWith("The Windows service is stopped. Start it. Use the service controls on this page, Windows Services", fixture.Shell.Availability.Guidance, StringComparison.Ordinal);
      fixture.Shell.ServiceControlGuidance = " ";
      await fixture.TickFailing(new OperationCanceledException("The reply did not arrive in time."));
      Assert.StartsWith("Use the service controls on this page", fixture.Shell.Availability.Guidance, StringComparison.Ordinal);
      Assert.Equal(ServiceAvailabilityKind.NotReached, fixture.Shell.Availability.Kind);

      await fixture.Tick(Replies.Text("Error: Unknown command."));
      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.OlderService, "The Windows service is older than this companion and does not know its requests.",
         "Finish the update: install the service in the same version as this companion. This window keeps trying."), fixture.Shell.Availability);

      await fixture.Tick(Replies.Text("""{"v":3,"ok":false,"error":{"code":"unsupported_version","message":"Version 3 only."}}"""));
      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.NewerService, "The Windows service is newer than this companion, which cannot read its replies.",
         "Update the companion to the service's version. Updates are in Settings."), fixture.Shell.Availability);

      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Empty(fixture.Wizard.Requests);
      Assert.False(fixture.Shell.Tree.AddServer.IsEnabled);
      Assert.Equal(fixture.Shell.Availability.Summary, fixture.Shell.Tree.AddServer.DisabledReason);
      Assert.Equal(["NotReached", "OlderService", "NewerService"],
         fixture.Log.Entries.Where(entry => entry.StartsWith("WARN The local print service is not available", StringComparison.Ordinal)).Select(entry => entry.Split('(', ')')[1]), StringComparer.Ordinal);
   }



   /// <summary>A reply that cannot be read, and a refusal to give a snapshot, are each said as what they are; a refusal shows the service's reason, which the contract has already made printable.</summary>
   [Fact]
   public async Task UnreadableReply_AndARefusal_AreSaidAsWhatTheyAre()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(Replies.Text("""{"v":2,"ok":true,"result":{"token":"something else"}}"""));
      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.Unreadable, "The local print service answered, and the answer could not be read.",
         "Install matching versions of the companion and the service. This window keeps trying."), fixture.Shell.Availability);

      await fixture.Tick(Replies.Failure(ManagementErrorCode.InternalError, "The journal\u202E is locked."));
      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.Refused, "The local print service refused to report its state: The journal  is locked.",
         "See Logs for the reason. This window keeps trying."), fixture.Shell.Availability);
      Assert.Equal("Local service needs attention", fixture.Shell.Footer.Text);
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);

      await fixture.Tick(Replies.Text("""{"v":2,"ok":false,"error":{"code":"internal_error","message":""}}"""));
      Assert.Equal("The local print service refused to report its state.", fixture.Shell.Availability.Summary);
   }



   /// <summary>While an update is installed the service is reached and its stored data is ready, so the window works as usual; the Computer page says that printing is paused and until when the pause lasts by itself.</summary>
   [Fact]
   public async Task UpdatePaused_SaysUntilWhen()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(new ServiceSnapshot(Snapshots.Computer(update: new UpdateState(true, new DateTimeOffset(2026, 11, 10, 15, 14, 5, TimeSpan.Zero))), [Snapshots.Server(1)]));

      Assert.Equal(new ServiceAvailability(ServiceAvailabilityKind.UpdatePaused, "Printing is paused while an update is installed.",
         "If the update does not finish, the pause ends by itself at 11/10/2026 17:14:05."), fixture.Shell.Availability);
      Assert.True(fixture.Shell.Availability.StoredDataReady);
      Assert.Equal(PageKey.ForServer(PageKind.Printers, Snapshots.Id(1)), fixture.Shell.Selected);
      Assert.True(fixture.Shell.Tree.AddServer.IsEnabled);
      Assert.Equal("Connected and ready", fixture.Shell.Footer.Text);
      Assert.Contains("INFO The local print service answered a snapshot (UpdatePaused).", fixture.Log.Entries, StringComparer.Ordinal);

      await fixture.Tick(new ServiceSnapshot(Snapshots.Computer(update: new UpdateState(true)), [Snapshots.Server(1)]));
      Assert.Equal("The pause ends when the update has finished.", fixture.Shell.Availability.Guidance);
   }



   /// <summary>The service's healthy golden snapshot: reached, two servers in order, the second with its certificate exception on, and the window on the first server's Printers page.</summary>
   [Fact]
   public async Task GoldenHealthySnapshot_IsReached_WithItsServers()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(Replies.Golden("snapshot.healthy.reply.json"));

      Assert.Equal(ServiceAvailabilityKind.Reached, fixture.Shell.Availability.Kind);
      Assert.Equal("Warehouse,Test bench", fixture.ServerNames);
      Assert.Equal(["Connected and ready", "Not paired"], fixture.Shell.Tree.Servers.Select(server => server.StatusText), StringComparer.Ordinal);
      Assert.Equal(["Connected. Waiting for print jobs.", "Signed in. Pair this computer to enable printing."], fixture.Shell.Tree.Servers.Select(server => server.Detail), StringComparer.Ordinal);
      Assert.Equal(["off", "Certificate exception on"], fixture.Shell.Tree.Servers.Select(server => server.ExceptionMarker ?? "off"), StringComparer.Ordinal);
      Assert.Equal(PageKey.ForServer(PageKind.Printers, "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f"), fixture.Shell.Selected);
      Assert.Equal("2 servers: 1 not ready, 1 ready", fixture.Shell.Footer.Text);
      Assert.Empty(fixture.Wizard.Requests);
   }



   /// <summary>A read that was skipped, and one whose wait had ended, say nothing new about the service and give no availability; everything else needs a result and a time format.</summary>
   [Fact]
   public async Task From_ASkippedRead_SaysNothing()
   {
      using ShellFixture fixture = new();
      fixture.Pipe.HoldEverything();
      Task[] inFlight = [.. Enumerable.Range(0, ManagementClient.MaximumRequestsInFlight).Select(index => fixture.Client.AcknowledgeJobAsync(Snapshots.Id(1), "job-" + index))];
      using CancellationTokenSource ended = new();
      await ended.CancelAsync();

      ServiceResult<ServiceSnapshot> skipped = await fixture.Client.SnapshotAsync();
      ServiceResult<ServiceSnapshot> notStarted = await fixture.Client.SnapshotAsync(cancellationToken: ended.Token);

      Assert.Equal((ServiceOutcome.Skipped, ServiceOutcome.Canceled), (skipped.Outcome, notStarted.Outcome));
      Assert.Null(ServiceAvailability.From(skipped, TestData.Time, null));
      Assert.Null(ServiceAvailability.From(notStarted, TestData.Time, "Guidance."));
      Assert.Throws<ArgumentNullException>(() => ServiceAvailability.From(null!, TestData.Time, null));
      Assert.Throws<ArgumentNullException>(() => ServiceAvailability.From(skipped, null!, null));
      Assert.All(inFlight, request => Assert.False(request.IsCompleted));
   }
}
