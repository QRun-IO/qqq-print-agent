/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Applying an update inside the staged helper. This step starts Windows Installer on a machine MSI with administrator
/// rights, so the tests check that every guard runs before the installer is started (who runs it, from where, on which
/// request, for which companion, on which bytes), what the installer is started with, what its answer is taken to
/// mean, and that the outcome is recorded, printing resumed and the companion reopened whatever happened. The
/// installer is a process of the fake: Windows Installer is never started.
/// </summary>
public sealed class MsiUpdateApplyTests
{
   /// <summary>How every failure before the installer finished begins.</summary>
   private const string Failed = "Update failed before completion: ";



   /// <summary>The file name of Windows Installer's program.</summary>
   private const string Installer = "msiexec.exe";



   /// <summary>The file name of the companion.</summary>
   private const string Companion = "Qqq.PrintAgent.exe";



   /// <summary>
   /// A verified MSI is installed by the system's own msiexec, started without a shell and with exactly the six
   /// arguments of a passive install that never restarts by itself and logs into the session folder. While the
   /// installer runs the MSI can be neither rewritten nor deleted. The helper returns the installer's exit code.
   /// </summary>
   [Fact]
   public async Task VerifiedMsiIsInstalledByTheSystemInstallerWhileItsBytesAreHeld()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      List<string> whileInstalling = [];
      setup.WhileStarting = start =>
      {
         if(IsInstaller(start))
         {
            whileInstalling.Add(Throws<IOException>(() => File.WriteAllBytes(setup.Package, [9, 9])) ? "cannot be rewritten" : "REWRITTEN");
            whileInstalling.Add(Throws<IOException>(() => File.Delete(setup.Package)) ? "cannot be deleted" : "DELETED");
         }
      };
      Assert.Equal(0, await setup.ApplyAsync(request));
      ProcessStartInfo installer = setup.Processes.Started[0];
      Assert.Equal(Path.Combine(Environment.SystemDirectory, Installer), installer.FileName);
      Assert.Equal(new[] { "/i", setup.Package, "/passive", "/norestart", "/L*v", Path.Combine(setup.Session, "install.log") }, installer.ArgumentList, StringComparer.Ordinal);
      Assert.Equal((false, true, ""), (installer.UseShellExecute, installer.CreateNoWindow, installer.Arguments));
      Assert.Equal(new[] { "cannot be rewritten", "cannot be deleted" }, whileInstalling, StringComparer.Ordinal);
      File.WriteAllBytes(setup.Package, [9, 9]);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>
   /// After a successful install the three installed programs are checked for the requested release, the outcome is
   /// recorded with the time, the release, the exit code and the message, and written to the activity log of the machine
   /// state folder as information; then printing is resumed and the installed companion reopened, in that order, the
   /// companion without a shell, in its own folder and with no arguments.
   /// </summary>
   [Fact]
   public async Task SuccessfulInstallIsVerifiedRecordedLoggedResumedAndTheCompanionReopened()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      File.WriteAllText(setup.OutcomePath, "an earlier outcome");
      Assert.Equal(0, await setup.ApplyAsync(request));
      const string message = "Update installed successfully. Open the companion to verify service status.";
      Assert.Equal(new[] { setup.Places.InstalledCompanion, setup.Places.InstalledService, setup.InstalledTool }, setup.Processes.VersionsRead, StringComparer.Ordinal);
      JsonElement outcome = JsonDocument.Parse(File.ReadAllText(setup.OutcomePath)).RootElement;
      Assert.Equal((MsiUpdateSetup.Now, MsiUpdateSetup.Version, 0, message),
         (outcome.GetProperty("completedAt").GetDateTimeOffset(), outcome.GetProperty("version").GetString(), outcome.GetProperty("exitCode").GetInt32(), outcome.GetProperty("message").GetString()));
      Assert.Equal(new[] { Path.GetFileName(setup.OutcomePath) }, Directory.EnumerateFiles(setup.Places.UpdatesDirectory).Select(file => Path.GetFileName(file)).ToArray());
      Assert.Equal("2026-11-10 12:00:00Z — " + message, setup.Steps.ReadLastOutcomeMessage());
      Assert.Equal(new[] { setup.State }, setup.LogsOpened, StringComparer.Ordinal);
      Assert.Equal(new[] { "INFO [Update] " + message }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(new[] { "start " + Installer, "send resume-after-update", "start " + Companion }, setup.Journal, StringComparer.Ordinal);
      ProcessStartInfo companion = setup.Processes.Started[1];
      Assert.Equal((setup.Places.InstalledCompanion, false, Path.GetDirectoryName(setup.Places.InstalledCompanion), 0, ""),
         (companion.FileName, companion.UseShellExecute, companion.WorkingDirectory, companion.ArgumentList.Count, companion.Arguments));
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>
   /// The helper waits for Windows Installer however long it takes, holding the MSI, and only then records an outcome;
   /// nothing in the rules can end the installer.
   /// </summary>
   [Fact]
   public async Task HelperWaitsForTheInstallerToFinish()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      FakeProcess running = new(id: 7100, Installer, startTicks: 2);
      setup.Processes.OnStart = start => IsInstaller(start) ? running : FakeProcessHost.Ended(start.FileName, 0);
      Task<int> apply = setup.ApplyAsync(request);
      await MsiUpdateSetup.UntilAsync(() => running.Waits == 1);
      await Task.Delay(50);
      Assert.False(apply.IsCompleted);
      Assert.False(File.Exists(setup.OutcomePath));
      Assert.True(Throws<IOException>(() => File.WriteAllBytes(setup.Package, [9, 9])));
      Assert.Empty(setup.Sent);
      running.Exit(1602);
      Assert.Equal(1602, await apply);
      Assert.Equal(new[] { "resume-after-update" }, setup.Sent, StringComparer.Ordinal);
   }



   /// <summary>
   /// An exit code other than 0 is returned as it is and described for what it is: a required restart is logged as
   /// information and says the new version is not yet fully active, a cancellation and any other code are logged as
   /// errors. The installed versions are checked only after exit code 0. Printing is resumed and the companion reopened
   /// in every case.
   /// </summary>
   [Theory]
   [InlineData(3010, "INFO", "Update installed, but Windows requires a restart to finish replacing files. Restart Windows before treating the new version as fully active.")]
   [InlineData(1602, "ERROR", "Windows Installer canceled the update. The previous installation may remain; inspect the version and service status before retrying.")]
   [InlineData(1603, "ERROR", "Windows Installer did not report a completed update (exit 1603). Inspect the private installation log and service status before retrying.")]
   public async Task OtherExitCodesAreReturnedAndDescribedForWhatTheyAre(int code, string level, string message)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      setup.ExitCodes[Installer] = code;
      Assert.Equal(code, await setup.ApplyAsync(request));
      Assert.Empty(setup.Processes.VersionsRead);
      Assert.Equal(new[] { level + " [Update] " + message }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(code, JsonDocument.Parse(File.ReadAllText(setup.OutcomePath)).RootElement.GetProperty("exitCode").GetInt32());
      Assert.Equal(new[] { "start " + Installer, "send resume-after-update", "start " + Companion }, setup.Journal, StringComparer.Ordinal);
   }



   /// <summary>
   /// Exit code 0 is not yet success: when an installed program does not carry the requested release, or cannot be
   /// read, the helper returns 1 and records an error that names the program and says the installer returned success.
   /// </summary>
   [Theory]
   [InlineData("0.3.1", "Windows Installer returned success, but the installed Qqq.PrintAgent.Service.exe version does not match the requested release. Inspect the installation log and repair the package.")]
   [InlineData(null, "Windows Installer returned success, but the installed Qqq.PrintAgent.Service.exe version could not be verified. Inspect the installation log and repair the package.")]
   public async Task InstallThatLeftAnotherVersionIsAFailure(string? serviceVersion, string message)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      if(serviceVersion is null)
      { _ = setup.Processes.Versions.Remove(setup.Places.InstalledService); }
      else
      { setup.Processes.Versions[setup.Places.InstalledService] = serviceVersion; }
      Assert.Equal(1, await setup.ApplyAsync(request));
      Assert.Equal(new[] { "ERROR [Update] " + Failed + message }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(new[] { "start " + Installer, "send resume-after-update", "start " + Companion }, setup.Journal, StringComparer.Ordinal);
   }



   /// <summary>
   /// Someone who is not an administrator cannot apply an update: the helper returns 1 and the installer is never
   /// started. The outcome cannot be recorded either, since that needs the same rights, but printing is still resumed
   /// and the companion reopened.
   /// </summary>
   [Fact]
   public async Task NonAdministratorCannotApply()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      setup.Security.IsAdministrator = false;
      Assert.Equal(1, await setup.ApplyAsync(request));
      Assert.Equal(new[] { "send resume-after-update", "start " + Companion }, setup.Journal, StringComparer.Ordinal);
      Assert.False(File.Exists(setup.OutcomePath));
      Assert.Empty(setup.LogsOpened);
   }



   /// <summary>
   /// Each thing that must hold before the installer may be started stops the update when it does not: the helper
   /// returns 1, records why with the release it could read, and Windows Installer is never started.
   /// </summary>
   [Theory]
   [InlineData("request-emptied", "unknown", "Empty update request.")]
   [InlineData("request-writable", "unknown", "An update path allows modification by an untrusted identity. Repair the MSI directory permissions.")]
   [InlineData("not-the-helper", MsiUpdateSetup.Version, "Apply must run from the verified staged helper copy.")]
   [InlineData("no-program-path", MsiUpdateSetup.Version, "Apply must run from the verified staged helper copy.")]
   [InlineData("helper-writable", MsiUpdateSetup.Version, "An update path allows modification by an untrusted identity. Repair the MSI directory permissions.")]
   [InlineData("helper-missing", MsiUpdateSetup.Version, "A required staged update file is missing.")]
   [InlineData("msi-changed", MsiUpdateSetup.Version, "The staged update changed or failed its checksum. Download it again.")]
   [InlineData("log-is-a-junction", MsiUpdateSetup.Version, "Update paths cannot contain symbolic links or junctions.")]
   public async Task UpdateThatFailsAGuardNeverStartsTheInstaller(string broken, string recordedVersion, string reason)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      switch(broken)
      {
         case "request-emptied":
            File.WriteAllText(request, "null");
            break;
         case "request-writable":
            setup.Security.Descriptors[request] = MsiUpdateSetup.WritableByUsers();
            break;
         case "not-the-helper":
            setup.Processes.CurrentPath = setup.InstalledTool;
            break;
         case "no-program-path":
            setup.Processes.CurrentPath = null;
            break;
         case "helper-writable":
            setup.Security.Descriptors[setup.Helper] = MsiUpdateSetup.WritableByUsers();
            break;
         case "helper-missing":
            File.Move(setup.Helper, setup.Helper + ".moved");
            break;
         case "msi-changed":
            File.WriteAllBytes(setup.Package, [.. setup.PackageBytes.Reverse()]);
            break;
         default:
            Directory.CreateDirectory(Path.Combine(setup.Root, "elsewhere"));
            MsiUpdateSetup.MakeJunction(Path.Combine(setup.Session, "install.log"), Path.Combine(setup.Root, "elsewhere"));
            break;
      }
      Assert.Equal(1, await setup.ApplyAsync(request));
      Assert.DoesNotContain(Installer, setup.StartedPrograms, StringComparer.Ordinal);
      JsonElement outcome = JsonDocument.Parse(File.ReadAllText(setup.OutcomePath)).RootElement;
      Assert.Equal((recordedVersion, 1, Failed + reason), (outcome.GetProperty("version").GetString(), outcome.GetProperty("exitCode").GetInt32(), outcome.GetProperty("message").GetString()));
      Assert.Equal(new[] { "ERROR [Update] " + Failed + reason }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(new[] { "send resume-after-update", "start " + Companion }, setup.Journal, StringComparer.Ordinal);
   }



   /// <summary>
   /// The process with the companion's id must still be the companion that asked: one that started at another time, or
   /// that runs another program, has taken over the id, and the installer is not started.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task ProcessThatIsNoLongerTheCompanionThatAskedStopsTheUpdate(bool startedAtAnotherTime)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper(companionClosed: false);
      if(startedAtAnotherTime)
      { setup.Companion.StartTicks++; }
      else
      { setup.Companion.Program = setup.InstalledTool; }
      Assert.Equal(1, await setup.ApplyAsync(request));
      Assert.DoesNotContain(Installer, setup.StartedPrograms, StringComparer.Ordinal);
      Assert.Equal(new[] { "ERROR [Update] " + Failed + "The expected companion process identity changed. No installer was started." }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(0, setup.Companion.Waits);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>
   /// A companion that is still open is waited for, and the installer is started only once it has closed. The companion
   /// is recognized whatever the case of the path Windows reports for it.
   /// </summary>
   [Fact]
   public async Task InstallerIsStartedOnlyOnceTheCompanionHasClosed()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper(companionClosed: false);
      setup.Companion.Program = setup.Places.InstalledCompanion.ToUpperInvariant();
      Task<int> apply = setup.ApplyAsync(request);
      await MsiUpdateSetup.UntilAsync(() => setup.Companion.Waits == 1);
      await Task.Delay(50);
      Assert.False(apply.IsCompleted);
      Assert.Empty(setup.Processes.Started);
      setup.Companion.Exit(0);
      Assert.Equal(0, await apply);
      Assert.Equal(new[] { Installer, Companion }, setup.StartedPrograms, StringComparer.Ordinal);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>
   /// The MSI is checked against the request and held only once the companion has closed, not while it is waited
   /// for: during the wait the MSI is not yet held, and bytes that change in it then are found when it is checked
   /// afterwards, so the installer is never started on them. This records the order the helper works in today.
   /// </summary>
   [Fact]
   public async Task MsiIsCheckedAndHeldOnlyOnceTheCompanionHasClosed()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper(companionClosed: false);
      Task<int> apply = setup.ApplyAsync(request);
      await MsiUpdateSetup.UntilAsync(() => setup.Companion.Waits == 1);
      Assert.False(Throws<IOException>(() => File.WriteAllBytes(setup.Package, [.. setup.PackageBytes.Reverse()])), "The MSI was already held while the companion was still waited for.");
      setup.Companion.Exit(0);
      Assert.Equal(1, await apply);
      Assert.DoesNotContain(Installer, setup.StartedPrograms, StringComparer.Ordinal);
      Assert.Equal(new[] { "ERROR [Update] " + Failed + "The staged update changed or failed its checksum. Download it again." }, setup.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>
   /// A companion that does not close is waited for for 30 seconds and no longer: just before that the helper is still
   /// waiting and has started nothing; at 30 seconds it gives up, says that no process was terminated, and the installer
   /// is never started. The companion is left running.
   /// </summary>
   [Fact]
   public async Task CompanionThatStaysOpenForThirtySecondsStopsTheUpdate()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper(companionClosed: false);
      Task<int> apply = setup.ApplyAsync(request);
      await MsiUpdateSetup.UntilAsync(() => setup.Companion.Waits == 1 && setup.Clock.PendingTimers == 1);
      Assert.True(setup.Clock.HasTimerDueIn(TimeSpan.FromSeconds(30)));
      setup.Clock.Advance(TimeSpan.FromSeconds(29.9));
      await Task.Delay(50);
      Assert.False(apply.IsCompleted);
      Assert.Empty(setup.Processes.Started);
      setup.Clock.Advance(TimeSpan.FromSeconds(0.1));
      Assert.Equal(1, await apply);
      Assert.DoesNotContain(Installer, setup.StartedPrograms, StringComparer.Ordinal);
      Assert.Equal(new[] { "ERROR [Update] " + Failed + "The companion did not close within 30 seconds. Close it and retry the update; no process was terminated." }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.False(setup.Companion.HasExited);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>
   /// A companion that has already closed is not waited for, whether its process is gone, has ended, or ends while it
   /// is looked at and reading its identity fails with an invalid-operation failure for that reason; the installer is
   /// started.
   /// </summary>
   [Theory]
   [InlineData("gone")]
   [InlineData("ended")]
   [InlineData("ends-and-the-read-says-so")]
   public async Task CompanionThatHasAlreadyClosedIsNotWaitedFor(string how)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper(companionClosed: string.Equals(how, "gone", StringComparison.Ordinal));
      if(string.Equals(how, "ended", StringComparison.Ordinal))
      { setup.Companion.Exit(0); }
      if(string.Equals(how, "ends-and-the-read-says-so", StringComparison.Ordinal))
      {
         setup.Companion.ExitsWhenInspected = true;
         setup.Companion.InspectionFailure = new InvalidOperationException("Synthetic: cannot process request because the process has exited.");
      }
      Assert.Equal(0, await setup.ApplyAsync(request));
      Assert.Equal(new[] { Installer, Companion }, setup.StartedPrograms, StringComparer.Ordinal);
      Assert.Equal(0, setup.Companion.Waits);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>
   /// A companion that closes at the very moment its program is read is reported by .NET 10 with Windows error 299,
   /// not as an invalid operation, and the helper does not take that for a closed companion. The update stops there,
   /// with no installer started, and can be run again. This records what the helper does today.
   /// </summary>
   [Fact]
   public async Task CompanionThatClosesWhileItsProgramIsReadStopsTheUpdateWithoutInstalling()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper(companionClosed: false);
      setup.Companion.ExitsWhenInspected = true;
      Assert.Equal(1, await setup.ApplyAsync(request));
      Assert.True(setup.Companion.HasExited);
      Assert.DoesNotContain(Installer, setup.StartedPrograms, StringComparer.Ordinal);
      Assert.Equal(new[] { "ERROR [Update] " + Failed + "Synthetic: only part of a ReadProcessMemory or WriteProcessMemory request was completed." }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>A running process with the companion's id that Windows will not let the helper inspect cannot be confirmed as the companion, so the installer is not started.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task CompanionThatCannotBeInspectedStopsTheUpdate(bool accessDenied)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper(companionClosed: false);
      setup.Companion.InspectionFailure = accessDenied ? new Win32Exception(5, "Synthetic: access is denied.") : new InvalidOperationException("Synthetic: the process cannot be inspected.");
      Assert.Equal(1, await setup.ApplyAsync(request));
      Assert.DoesNotContain(Installer, setup.StartedPrograms, StringComparer.Ordinal);
      Assert.Equal(new[] { "ERROR [Update] " + Failed + setup.Companion.InspectionFailure.Message }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>When Windows starts nothing for the installer the update fails and says so; printing is resumed and the companion reopened.</summary>
   [Fact]
   public async Task InstallerThatDoesNotStartFailsTheUpdate()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      _ = setup.StartsNothing.Add(Installer);
      Assert.Equal(1, await setup.ApplyAsync(request));
      Assert.Equal(new[] { "ERROR [Update] " + Failed + "Windows Installer could not start." }, setup.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(new[] { "start " + Installer, "send resume-after-update", "start " + Companion }, setup.Journal, StringComparer.Ordinal);
      File.WriteAllBytes(setup.Package, [9, 9]);
   }



   /// <summary>
   /// The three closing steps do not depend on each other or on the result: a log that cannot be written, a service
   /// that cannot be reached and a companion that cannot be started each leave the others to run and the exit code as
   /// it was.
   /// </summary>
   [Theory]
   [InlineData("log")]
   [InlineData("pipe")]
   [InlineData("companion")]
   public async Task ClosingStepsDoNotDependOnEachOther(string failing)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      setup.ExitCodes[Installer] = 3010;
      setup.Log.Failure = string.Equals(failing, "log", StringComparison.Ordinal) ? new IOException("Synthetic: the log's disk failed.") : null;
      setup.PipeFailure = string.Equals(failing, "pipe", StringComparison.Ordinal) ? new TimeoutException("Synthetic: the service did not answer.") : null;
      if(string.Equals(failing, "companion", StringComparison.Ordinal))
      { _ = setup.StartsNothing.Add(Companion); }
      Assert.Equal(3010, await setup.ApplyAsync(request));
      Assert.Equal(new[] { "start " + Installer, "send resume-after-update", "start " + Companion }, setup.Journal, StringComparer.Ordinal);
      Assert.True(File.Exists(setup.OutcomePath));
   }



   /// <summary>The companion is reopened only from an install folder that can be trusted: when ordinary users may write to the companion or to a folder above it, nothing is started in its place.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(1)]
   [InlineData(2)]
   [InlineData(3)]
   public async Task CompanionIsNotReopenedFromAFolderOrdinaryUsersMayWriteTo(int level)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.PrepareAndRunAsHelper();
      setup.Security.Descriptors[new[] { setup.Places.InstalledCompanion, Path.GetDirectoryName(setup.Places.InstalledCompanion)!, setup.Places.InstalledRoot, setup.ProgramFiles }[level]] = MsiUpdateSetup.WritableByUsers();
      Assert.Equal(0, await setup.ApplyAsync(request));
      Assert.Equal(new[] { "start " + Installer, "send resume-after-update" }, setup.Journal, StringComparer.Ordinal);
   }



   /// <summary>Without a recorded outcome there is nothing to show.</summary>
   [Fact]
   public void NoRecordedOutcomeShowsNothing()
   {
      using var setup = new MsiUpdateSetup();
      Assert.Null(setup.Steps.ReadLastOutcomeMessage());
      Directory.CreateDirectory(setup.Places.UpdatesDirectory);
      File.WriteAllText(setup.OutcomePath, "null");
      Assert.Null(setup.Steps.ReadLastOutcomeMessage());
   }



   /// <summary>
   /// A recorded outcome that cannot be trusted is not shown: one larger than 16 KiB is called invalid, and one that is
   /// not an outcome, that ordinary users may write to, or that is read by someone who is not an administrator is
   /// reported as unreadable, with nothing of its content.
   /// </summary>
   [Theory]
   [InlineData("oversize", "The previous update outcome is invalid. Inspect the protected update logs.")]
   [InlineData("garbage", "The previous update outcome could not be read. Inspect the protected update logs.")]
   [InlineData("writable", "The previous update outcome could not be read. Inspect the protected update logs.")]
   [InlineData("not-administrator", "The previous update outcome could not be read. Inspect the protected update logs.")]
   public void RecordedOutcomeThatCannotBeTrustedIsNotShown(string broken, string shown)
   {
      using var setup = new MsiUpdateSetup();
      const string valid = """{"completedAt":"2026-11-10T12:00:00+00:00","version":"0.3.2-rc.1","exitCode":0,"message":"planted text"}""";
      File.WriteAllText(setup.OutcomePath, broken switch
      {
         "oversize" => valid + new string(' ', 16 * 1024),
         "garbage" => "planted text that is not an outcome",
         _ => valid,
      });
      if(broken is "writable" or "not-administrator")
      {
         Assert.Equal("2026-11-10 12:00:00Z — planted text", setup.Steps.ReadLastOutcomeMessage());
         setup.Security.Descriptors[setup.OutcomePath] = string.Equals(broken, "writable", StringComparison.Ordinal) ? MsiUpdateSetup.WritableByUsers() : FakePathSecurity.Descriptor();
         setup.Security.IsAdministrator = !string.Equals(broken, "not-administrator", StringComparison.Ordinal);
      }
      Assert.Equal(shown, setup.Steps.ReadLastOutcomeMessage());
   }



   /// <summary>True when the start is of Windows Installer's program.</summary>
   private static bool IsInstaller(ProcessStartInfo start) => string.Equals(Path.GetFileName(start.FileName), Installer, StringComparison.OrdinalIgnoreCase);



   /// <summary>True when the action fails with the given kind of exception.</summary>
   private static bool Throws<T>(Action action) where T : Exception
   {
      try
      {
         action();
         return false;
      }
      catch(T)
      {
         return true;
      }
   }
}
