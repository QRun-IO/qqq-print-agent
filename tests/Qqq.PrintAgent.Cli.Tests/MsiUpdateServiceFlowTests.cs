/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using KofTwentyTwo.AppKit.Updates;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The in-app update as the companion drives it: check, download, prepare, ask the service to pause, hand over to the
/// helper and close. The tests run the real update steps in temporary folders against a release source, a pipe and
/// processes that exist only in memory, and check what is done in which order and what is undone when a step fails:
/// the pause is lifted only when it was asked for, what was staged is removed unless the helper already runs from it,
/// and nothing is installed that the last check did not find. No release feed, service or installer is reached.
/// </summary>
public sealed class MsiUpdateServiceFlowTests
{
   /// <summary>What a cancelled update says.</summary>
   private const string Canceled = "The update was canceled. The current version remains installed.";



   /// <summary>What an install is refused with when no check found a release.</summary>
   private const string CheckFirst = "Check for updates from the installed app before installing an update.";



   /// <summary>The file name of the helper the update is handed to.</summary>
   private const string Helper = "qqq-print-agent.exe";



   /// <summary>An in-app update is offered only to the companion at its install path on a computer that has the machine state folder; the path is compared without regard to case.</summary>
   [Theory]
   [InlineData("installed", true, true)]
   [InlineData("installed-other-case", true, true)]
   [InlineData("copy", true, false)]
   [InlineData("unknown", true, false)]
   [InlineData("installed", false, false)]
   public void UpdateIsOfferedOnlyToTheInstalledCompanion(string runningAs, bool machineState, bool offered)
   {
      using var flow = new Flow();
      flow.Setup.Processes.CurrentPath = runningAs switch
      {
         "installed" => flow.Setup.Places.InstalledCompanion,
         "installed-other-case" => flow.Setup.Places.InstalledCompanion.ToUpperInvariant(),
         "copy" => Path.Combine(flow.Setup.Root, "Downloads", "Qqq.PrintAgent.exe"),
         _ => null,
      };
      if(!machineState)
      { Directory.Delete(flow.Setup.State, recursive: true); }
      Assert.Equal(offered, flow.Service.IsSupported);
   }



   /// <summary>A copy that is not the installed companion is told to install the MSI, and the release feed is not asked.</summary>
   [Fact]
   public async Task CheckFromACopyIsRefusedWithoutAskingTheFeed()
   {
      using var flow = new Flow();
      flow.Setup.Processes.CurrentPath = Path.Combine(flow.Setup.Root, "Downloads", "Qqq.PrintAgent.exe");
      UpdateCheckResult result = await flow.Service.CheckAsync();
      Assert.Equal((null, "Install the machine MSI to check for updates from this app."), (result.AvailableVersion, result.Error));
      Assert.Empty(flow.Releases.Checks);
      Assert.Equal(CheckFirst, await flow.Service.DownloadAndApplyAsync());
   }



   /// <summary>A check asks the feed with this build's version and reports the newer release it finds, or that there is none.</summary>
   [Fact]
   public async Task CheckReportsTheNewerReleaseOrThatThereIsNone()
   {
      using var flow = new Flow();
      UpdateCheckResult found = await flow.Service.CheckAsync();
      Assert.Equal((Flow.Release.Version, null, true), (found.AvailableVersion, found.Error, found.IsUpdateAvailable));
      flow.Releases.Offered = null;
      UpdateCheckResult none = await flow.Service.CheckAsync();
      Assert.Equal((null, null, false), (none.AvailableVersion, none.Error, none.IsUpdateAvailable));
      string thisBuild = typeof(MsiUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
      Assert.Equal(thisBuild, flow.Service.CurrentVersion);
      Assert.Equal(new[] { thisBuild, thisBuild }, flow.Releases.Checks, StringComparer.Ordinal);
   }



   /// <summary>
   /// A check that fails is reported as a message, with anything shaped like a credential taken out, and a cancelled
   /// one as cancelled. Either way the release an earlier check found is forgotten, so nothing can be installed until
   /// a check succeeds again; the same holds for a check that finds nothing.
   /// </summary>
   [Theory]
   [InlineData("failed", "The update server refused: token=[REDACTED]")]
   [InlineData("canceled", Canceled)]
   [InlineData("none", null)]
   public async Task CheckThatFindsNothingOrFailsLeavesNothingToInstall(string outcome, string? error)
   {
      using var flow = new Flow();
      Assert.True((await flow.Service.CheckAsync()).IsUpdateAvailable);
      flow.Releases.CheckFailure = outcome switch
      {
         "failed" => new HttpRequestException("The update server refused: token=synthetic-secret-value"),
         "canceled" => new OperationCanceledException("Synthetic: the check was cancelled."),
         _ => null,
      };
      flow.Releases.Offered = string.Equals(outcome, "none", StringComparison.Ordinal) ? null : Flow.Release;
      UpdateCheckResult result = await flow.Service.CheckAsync();
      Assert.Equal((null, error), (result.AvailableVersion, result.Error));
      Assert.Equal(CheckFirst, await flow.Service.DownloadAndApplyAsync());
      Assert.Empty(flow.Releases.Downloads);
      Assert.Empty(flow.Setup.Journal);
   }



   /// <summary>Without a check there is nothing to install: nothing is downloaded and the service is not asked for anything.</summary>
   [Fact]
   public async Task InstallWithoutACheckIsRefused()
   {
      using var flow = new Flow();
      Assert.Equal(CheckFirst, await flow.Service.DownloadAndApplyAsync());
      Assert.Empty(flow.Releases.Downloads);
      Assert.Empty(flow.Setup.Journal);
   }



   /// <summary>A release found while this was the installed companion is not installed once it no longer is: the install is refused and nothing is downloaded.</summary>
   [Fact]
   public async Task InstallIsRefusedWhenThisIsNoLongerTheInstalledCompanion()
   {
      using var flow = new Flow();
      Assert.True((await flow.Service.CheckAsync()).IsUpdateAvailable);
      Directory.Delete(flow.Setup.State, recursive: true);
      Assert.Equal(CheckFirst, await flow.Service.DownloadAndApplyAsync());
      Assert.Empty(flow.Releases.Downloads);
      Assert.Empty(flow.Setup.Journal);
   }



   /// <summary>
   /// An update goes: download into a new session folder inside the updates folder, prepare the helper for this
   /// companion's own process, ask the service to finish printing and pause, start the copied helper with the request,
   /// log the hand-off, close the companion. The service is asked nothing else, and the session stays for the helper.
   /// </summary>
   [Fact]
   public async Task UpdateIsDownloadedPreparedPausedHandedToTheHelperAndTheCompanionClosed()
   {
      using var flow = new Flow();
      _ = await flow.Service.CheckAsync();
      Assert.Null(await flow.Service.DownloadAndApplyAsync());
      Assert.Equal(new[] { "download", "send prepare-update", "start " + Helper, "exit" }, flow.Setup.Journal, StringComparer.Ordinal);
      (MsiRelease release, string currentVersion, string session) = Assert.Single(flow.Releases.Downloads);
      Assert.Equal((Flow.Release, flow.Service.CurrentVersion), (release, currentVersion));
      Assert.Equal(flow.Setup.Places.UpdatesDirectory, Path.GetDirectoryName(session));
      Assert.Matches("^[0-9a-f]{32}$", Path.GetFileName(session));
      string requestPath = Path.Combine(session, "request.json");
      System.Diagnostics.ProcessStartInfo start = Assert.Single(flow.Setup.Processes.Started);
      Assert.Equal(Path.Combine(session, "helper", Helper), start.FileName);
      Assert.Equal(new[] { "--apply-update", requestPath }, start.ArgumentList, StringComparer.Ordinal);
      MsiUpdateInstaller.InstallRequest request = JsonSerializer.Deserialize<MsiUpdateInstaller.InstallRequest>(File.ReadAllText(requestPath), PrintApiClient.JsonOptions)!;
      Assert.Equal((Flow.Release.Version, Convert.ToHexString(SHA256.HashData(flow.Releases.Content)), MsiUpdateSetup.CompanionId, MsiUpdateSetup.CompanionStarted, session),
         (request.Version, request.Sha256, request.ParentProcessId, request.ParentStartTicks, Path.GetDirectoryName(request.PackagePath)));
      Assert.Equal(new[] { "INFO [Companion] Verified update handed to Windows Installer; closing the companion." }, flow.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(1, flow.Exits);
   }



   /// <summary>
   /// The companion is told what is happening, step by step and in order, by the service itself, and the download's
   /// progress arrives as whole percentages, both as status text and through the progress the caller passed.
   /// </summary>
   [Fact]
   public async Task EachStepAndTheDownloadProgressAreReported()
   {
      using var flow = new Flow();
      _ = await flow.Service.CheckAsync();
      Collected<int> percentages = new();
      Assert.Null(await flow.Service.DownloadAndApplyAsync(percentages));
      await MsiUpdateSetup.UntilAsync(() => percentages.Snapshot().Length == 2 && flow.Statuses.Snapshot().Length == 5);
      Assert.Equal(new[] { 50, 100 }, percentages.Snapshot().Order());
      Assert.Equal(new[] { "Downloading the update…", "Update verified. Preparing Windows Installer…", "Waiting for active printing to finish…" },
         flow.Statuses.Snapshot().Where(status => !status.StartsWith("Downloading the update: ", StringComparison.Ordinal)), StringComparer.Ordinal);
      Assert.Equal(new[] { "Downloading the update: 100%", "Downloading the update: 50%" }, flow.Statuses.Snapshot().Where(status => status.StartsWith("Downloading the update: ", StringComparison.Ordinal)).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.All(flow.Senders.Snapshot(), sender => Assert.Same(flow.Service, sender));
   }



   /// <summary>
   /// When the service does not answer that it is ready, the helper is not started: the operator is told what the
   /// service said, the service is told to resume printing, what was staged is removed and the companion stays open.
   /// The answer must be exactly "Ready for update."; anything else, however close, is not readiness.
   /// </summary>
   [Theory]
   [InlineData("Not ready for update: 2 job(s) were still in progress after 25 seconds. They will finish; try the update again afterwards.", "The update was not started, and the current version remains installed. Not ready for update: 2 job(s) were still in progress after 25 seconds. They will finish; try the update again afterwards.")]
   [InlineData("Error: Unknown command.", "The service could not prepare for an update, and the current version remains installed. The service answered: Error: Unknown command.")]
   [InlineData("ready for update.", "The service could not prepare for an update, and the current version remains installed. The service answered: ready for update.")]
   public async Task ServiceThatIsNotReadyStopsTheUpdateAndIsToldToResume(string reply, string shown)
   {
      using var flow = new Flow();
      flow.Setup.Replies["prepare-update"] = reply;
      _ = await flow.Service.CheckAsync();
      Assert.Equal(shown, await flow.Service.DownloadAndApplyAsync());
      Assert.Equal(new[] { "download", "send prepare-update", "send resume-after-update" }, flow.Setup.Journal, StringComparer.Ordinal);
      Assert.False(Directory.Exists(flow.Session));
      Assert.Equal(0, flow.Exits);
      Assert.Empty(flow.Log.Entries);
   }



   /// <summary>A service that cannot be reached stops the update the same way; that the resume cannot be delivered either changes nothing, because the service ends an abandoned pause by itself.</summary>
   [Fact]
   public async Task ServiceThatCannotBeReachedStopsTheUpdate()
   {
      using var flow = new Flow();
      flow.Setup.PipeFailure = new TimeoutException("Synthetic: the service did not answer.");
      _ = await flow.Service.CheckAsync();
      Assert.Equal("Synthetic: the service did not answer.", await flow.Service.DownloadAndApplyAsync());
      Assert.Equal(new[] { "download", "send prepare-update", "send resume-after-update" }, flow.Setup.Journal, StringComparer.Ordinal);
      Assert.False(Directory.Exists(flow.Session));
      Assert.Equal(0, flow.Exits);
   }



   /// <summary>A retry after a stopped update stages afresh, in another session folder, and goes through.</summary>
   [Fact]
   public async Task RetryAfterAStoppedUpdateStagesAfresh()
   {
      using var flow = new Flow();
      flow.Setup.Replies["prepare-update"] = "Not ready for update: 1 job(s) were still in progress.";
      _ = await flow.Service.CheckAsync();
      Assert.NotNull(await flow.Service.DownloadAndApplyAsync());
      flow.Setup.Replies["prepare-update"] = "Ready for update.";
      Assert.Null(await flow.Service.DownloadAndApplyAsync());
      Assert.Equal(2, flow.Releases.Downloads.Select(download => download.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count());
      Assert.Equal(1, flow.Exits);
   }



   /// <summary>
   /// A failure before the service was asked to pause leaves printing alone: the service is asked nothing, what was
   /// staged is removed, and the operator gets the reason. That holds for a download that fails and for a preparation
   /// that is refused because the installed command line folder cannot be trusted.
   /// </summary>
   [Theory]
   [InlineData(true, "MSI integrity verification failed. No update was staged.")]
   [InlineData(false, "An update path allows modification by an untrusted identity. Repair the MSI directory permissions.")]
   public async Task FailureBeforeThePauseLeavesPrintingAloneAndNothingStaged(bool downloadFails, string reason)
   {
      using var flow = new Flow();
      if(downloadFails)
      { flow.Releases.DownloadFailure = new InvalidDataException(reason); }
      else
      { flow.Setup.Security.Descriptors[flow.Setup.Places.InstalledCli] = MsiUpdateSetup.WritableByUsers(); }
      _ = await flow.Service.CheckAsync();
      Assert.Equal(reason, await flow.Service.DownloadAndApplyAsync());
      Assert.Equal(new[] { "download" }, flow.Setup.Journal, StringComparer.Ordinal);
      Assert.False(Directory.Exists(flow.Session));
      Assert.Equal(0, flow.Exits);
   }



   /// <summary>
   /// The session folder is checked before it is created and again once it exists. An updates folder that cannot be
   /// trusted means no folder is created and nothing is downloaded; a session folder that turns out untrusted once it
   /// exists means nothing is downloaded into it.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task NothingIsDownloadedIntoAFolderThatCannotBeTrusted(bool onlyOnceCreated)
   {
      using var flow = new Flow();
      if(onlyOnceCreated)
      { flow.Setup.Security.Unlisted = path => string.Equals(Path.GetDirectoryName(path), flow.Setup.Places.UpdatesDirectory, StringComparison.OrdinalIgnoreCase) ? MsiUpdateSetup.WritableByUsers() : null; }
      else
      { flow.Setup.Security.Descriptors[flow.Setup.Places.UpdatesDirectory] = MsiUpdateSetup.WritableByUsers(); }
      Directory.Delete(flow.Setup.Session, recursive: true);
      _ = await flow.Service.CheckAsync();
      Assert.Equal("An update path allows modification by an untrusted identity. Repair the MSI directory permissions.", await flow.Service.DownloadAndApplyAsync());
      Assert.Empty(flow.Releases.Downloads);
      Assert.Empty(flow.Setup.Journal);
      Assert.Equal(onlyOnceCreated ? 1 : 0, Directory.GetDirectories(flow.Setup.Places.UpdatesDirectory).Length);
   }



   /// <summary>
   /// An update cancelled during the download or while the helper is being prepared says so, leaves printing alone
   /// and removes what was staged. One cancelled after the service has paused also tells the service to resume. In no
   /// case is the helper started.
   /// </summary>
   [Theory]
   [InlineData("downloading", false)]
   [InlineData("preparing", false)]
   [InlineData("pausing", true)]
   public async Task CancelledUpdateSaysSoAndUndoesWhatItDid(string during, bool paused)
   {
      using var flow = new Flow();
      using CancellationTokenSource cancel = new();
      flow.Releases.WhileDownloading += _ => Cancel("downloading");
      flow.Setup.Processes.WhileOpening = _ => Cancel("preparing");
      flow.Setup.WhileSending = _ => Cancel("pausing");
      _ = await flow.Service.CheckAsync();
      Assert.Equal(Canceled, await flow.Service.DownloadAndApplyAsync(progress: null, cancel.Token));
      Assert.Equal(paused ? new[] { "download", "send prepare-update", "send resume-after-update" } : ["download"], flow.Setup.Journal, StringComparer.Ordinal);
      Assert.False(Directory.Exists(flow.Session));
      Assert.Equal(0, flow.Exits);

      void Cancel(string step)
      {
         if(string.Equals(step, during, StringComparison.Ordinal))
         { cancel.Cancel(); }
      }
   }



   /// <summary>When Windows starts nothing for the helper, the update stops there: printing is resumed, what was staged is removed and the companion stays open.</summary>
   [Fact]
   public async Task HelperThatDoesNotStartStopsTheUpdateAndPrintingIsResumed()
   {
      using var flow = new Flow();
      _ = flow.Setup.StartsNothing.Add(Helper);
      _ = await flow.Service.CheckAsync();
      Assert.Equal("Could not start the staged update helper.", await flow.Service.DownloadAndApplyAsync());
      Assert.Equal(new[] { "download", "send prepare-update", "start " + Helper, "send resume-after-update" }, flow.Setup.Journal, StringComparer.Ordinal);
      Assert.False(Directory.Exists(flow.Session));
      Assert.Equal(0, flow.Exits);
   }



   /// <summary>
   /// Once the helper has started, a companion that cannot close is reported as exactly that and never as a failed
   /// install: printing stays paused for the installer and the session the helper runs from is left in place.
   /// </summary>
   [Fact]
   public async Task CompanionThatCannotCloseAfterTheHandOffIsNotReportedAsAFailedInstall()
   {
      using var flow = new Flow();
      flow.ExitFailure = new InvalidOperationException("Synthetic: the window is gone.");
      _ = await flow.Service.CheckAsync();
      Assert.Equal("The installer helper started, but the companion could not close. Check the update outcome and Logs before trying again.", await flow.Service.DownloadAndApplyAsync());
      Assert.Equal(new[] { "download", "send prepare-update", "start " + Helper, "exit" }, flow.Setup.Journal, StringComparer.Ordinal);
      Assert.True(File.Exists(Path.Combine(flow.Session, "helper", Helper)));
      Assert.True(File.Exists(Path.Combine(flow.Session, "request.json")));
   }



   /// <summary>
   /// A session that holds a junction, directly or in a folder below, is not deleted, because the delete could follow
   /// the junction out of the session: it is kept for an administrator, a warning says so, and what the junction points
   /// at is untouched. The update's own failure is still what the operator is told.
   /// </summary>
   [Theory]
   [InlineData("")]
   [InlineData("below")]
   public async Task SessionThatHoldsAJunctionIsKeptAndReported(string folder)
   {
      using var flow = new Flow();
      string elsewhere = Path.Combine(flow.Setup.Root, "elsewhere");
      Directory.CreateDirectory(elsewhere);
      File.WriteAllText(Path.Combine(elsewhere, "kept.txt"), "synthetic");
      flow.Releases.WhileDownloading += session =>
      {
         Directory.CreateDirectory(Path.Combine(session, folder));
         MsiUpdateSetup.MakeJunction(Path.Combine(session, folder, "linked"), elsewhere);
      };
      flow.Releases.DownloadFailure = new InvalidDataException("MSI integrity verification failed. No update was staged.");
      _ = await flow.Service.CheckAsync();
      Assert.Equal("MSI integrity verification failed. No update was staged.", await flow.Service.DownloadAndApplyAsync());
      Assert.True(Directory.Exists(Path.Combine(flow.Session, folder, "linked")));
      Assert.True(File.Exists(Path.Combine(elsewhere, "kept.txt")));
      Assert.Equal(new[] { "WARN [Companion] Unapplied update staging could not be removed: Unapplied update staging contains a link and was retained." }, flow.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// A session folder is checked again before it is deleted. One that ordinary users may meanwhile write to is not
   /// deleted from: it is kept for an administrator and a warning says why, while the operator is still told what
   /// stopped the update.
   /// </summary>
   [Fact]
   public async Task SessionThatCanNoLongerBeTrustedIsKeptAndReported()
   {
      using var flow = new Flow();
      flow.Releases.WhileDownloading += session => flow.Setup.Security.Descriptors[session] = MsiUpdateSetup.WritableByUsers();
      flow.Releases.DownloadFailure = new InvalidDataException("MSI integrity verification failed. No update was staged.");
      _ = await flow.Service.CheckAsync();
      Assert.Equal("MSI integrity verification failed. No update was staged.", await flow.Service.DownloadAndApplyAsync());
      Assert.True(Directory.Exists(flow.Session));
      Assert.Equal(new[] { "WARN [Companion] Unapplied update staging could not be removed: An update path allows modification by an untrusted identity. Repair the MSI directory permissions." }, flow.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>Releasing the update service releases its release source.</summary>
   [Fact]
   public void ReleasingTheServiceReleasesTheReleaseSource()
   {
      using var flow = new Flow();
      flow.Service.Dispose();
      Assert.True(flow.Releases.Disposed);
   }



   /// <summary>
   /// The companion's own constructor binds the installed product: the pipe is the installed service's and the update
   /// steps are the installed product's own. Only the binding shows that, because neither is called here. A test run
   /// is not the installed product, so the update is not offered, a check says to install the MSI and an install says
   /// to check first, all without the release feed, the service or this computer's machine state being reached. The
   /// asset prefix it is given is the release client's, which refuses one that is not letters and digits.
   /// </summary>
   [Fact]
   public async Task CompanionsConstructorBindsTheInstalledProduct()
   {
      FakeActivityLog log = new();
      using MsiUpdateService service = new(() => { }, log);
      Assert.Equal<Func<PipeRequest, Task<string>>>(AgentPipeClient.SendAsync, service.Send);
      Assert.Same(MsiUpdateInstaller.Installed, service.Steps);
      Assert.False(service.IsSupported);
      UpdateCheckResult check = await service.CheckAsync();
      Assert.Equal((null, "Install the machine MSI to check for updates from this app."), (check.AvailableVersion, check.Error));
      Assert.Equal(CheckFirst, await service.DownloadAndApplyAsync());
      Assert.Empty(log.Entries);
      ArgumentException refused = Assert.Throws<ArgumentException>(() => new MsiUpdateService(() => { }, log, "9-not-a-prefix"));
      Assert.Equal("assetPrefix", refused.ParamName);
   }



   /// <summary>Values reported from other threads, collected so a test can look at them at any time.</summary>
   private sealed class Collected<T> : IProgress<T>
   {
      /// <summary>What was reported so far.</summary>
      private readonly List<T> _values = [];



      /// <summary>Keeps one reported value.</summary>
      public void Report(T value)
      {
         lock(_values)
         { _values.Add(value); }
      }



      /// <summary>A copy of what was reported so far.</summary>
      internal T[] Snapshot()
      {
         lock(_values)
         { return [.. _values]; }
      }
   }



   /// <summary>
   /// One update service over the update steps of a <see cref="MsiUpdateSetup"/>, a release source that offers one
   /// newer release, and a service that answers that it is ready. What the release source and the companion's exit do
   /// is added to the setup's journal, so one list shows the order of everything that reached the outside.
   /// </summary>
   private sealed class Flow : IDisposable
   {
      /// <summary>The newer release the source offers. Its addresses are never fetched.</summary>
      internal static readonly MsiRelease Release = new("0.4.0", new Uri("https://github.com/QRun-IO/qqq-print-agent/releases/download/v0.4.0/QqqPrintAgent-v0.4.0-x64.msi"), new Uri("https://github.com/QRun-IO/qqq-print-agent/releases/download/v0.4.0/SHA256SUMS"), 7);



      /// <summary>Builds the service over the fakes and listens to what it reports.</summary>
      internal Flow()
      {
         Setup.Replies["prepare-update"] = "Ready for update.";
         Releases.WhileDownloading = _ => Setup.Journal.Add("download");
         Service = new MsiUpdateService(Exit, Log, Releases, Setup.SendAsync, Setup.Steps);
         Service.StatusChanged += (sender, status) =>
         {
            Senders.Report(sender);
            Statuses.Report(status.Message);
         };
      }



      /// <summary>The update steps in temporary folders, with their fakes.</summary>
      internal MsiUpdateSetup Setup { get; } = new();



      /// <summary>The release source.</summary>
      internal FakeMsiReleaseSource Releases { get; } = new() { Offered = Release };



      /// <summary>The companion's log.</summary>
      internal FakeActivityLog Log { get; } = new();



      /// <summary>The service under test.</summary>
      internal MsiUpdateService Service { get; }



      /// <summary>Every status text the service reported, in the order it arrived.</summary>
      internal Collected<string> Statuses { get; } = new();



      /// <summary>Who each status came from.</summary>
      internal Collected<object?> Senders { get; } = new();



      /// <summary>How many times the companion was told to close.</summary>
      internal int Exits { get; private set; }



      /// <summary>When set, the companion cannot close: telling it to fails with this.</summary>
      internal Exception? ExitFailure { get; set; }



      /// <summary>The session folder of the only download so far.</summary>
      internal string Session => Assert.Single(Releases.Downloads).Directory;



      /// <summary>Releases the service and removes the temporary folders.</summary>
      public void Dispose()
      {
         Service.Dispose();
         Setup.Dispose();
      }



      /// <summary>The companion's exit: it is noted, and fails when the test said so.</summary>
      private void Exit()
      {
         Exits++;
         Setup.Journal.Add("exit");
         if(ExitFailure is not null)
         { throw ExitFailure; }
      }
   }
}
