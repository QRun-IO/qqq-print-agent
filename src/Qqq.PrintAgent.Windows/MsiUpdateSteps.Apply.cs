/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Applying part of the updater, which runs only inside the staged helper copy. It re-verifies the request, waits for the
/// companion that asked to close, installs the verified MSI with Windows Installer, checks that all three installed programs
/// carry the requested release, records the outcome, and reopens the companion. It never kills a process or an installer.
/// </summary>
internal sealed partial class MsiUpdateSteps
{
   /// <summary>How long the helper waits for the companion that asked for the update to close before it gives up without installing.</summary>
   internal static readonly TimeSpan CompanionExitLimit = TimeSpan.FromSeconds(30);



   /// <summary>Runs only inside the protected copied helper; never kills an installer or an application.</summary>
   internal async Task<int> ApplyAsync(string requestPath)
   {
      int code = 1;
      string message;
      string version = "unknown";
      try
      {
         RequireAdministrator();
         MsiUpdateInstaller.InstallRequest request = ReadRequest(requestPath);
         version = request.Version;
         string directory = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
         string helper = Path.Combine(directory, "helper", "qqq-print-agent.exe");
         if(!string.Equals(processes.CurrentPath, helper, StringComparison.OrdinalIgnoreCase))
         {
            throw new InvalidOperationException("Apply must run from the verified staged helper copy.");
         }
         ValidateStagedFile(helper);
         await WaitForCompanionExitAsync(request).ConfigureAwait(false);
         // Retain this non-writable/non-deletable handle until Windows Installer exits.
         using FileStream packageLock = MsiUpdateInstaller.OpenVerifiedPackage(request.PackagePath, request.Sha256);
         string installLog = Path.Combine(directory, "install.log");
         MsiUpdateInstaller.ValidateNoReparseComponents(installLog);
         ProcessStartInfo start = MsiUpdateInstaller.CreateInstallerStart(request.PackagePath, installLog);
         using IRunningProcess installer = processes.Start(start) ?? throw new InvalidOperationException("Windows Installer could not start.");
         await installer.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
         code = installer.ExitCode;
         if(code == 0)
         {
            ValidateInstalledVersions(request.Version, processes.ProductVersion);
         }
         message = MsiUpdateInstaller.DescribeExitCode(code);
      }
      catch(Exception exception)
      {
         code = 1;
         message = "Update failed before completion: " + AgentActivityLog.Redact(exception.Message);
      }
      await CompleteAsync(version, code, message).ConfigureAwait(false);
      return code;
   }



   /// <summary>
   /// Waits up to 30 seconds for the companion that requested the update to exit, so its files can be replaced. It first
   /// confirms the process is still the same one (same start time and the installed executable); a different process
   /// aborts the update before any installer starts. It never terminates the companion, and a timeout is reported.
   /// </summary>
   private async Task WaitForCompanionExitAsync(MsiUpdateInstaller.InstallRequest request)
   {
      IRunningProcess parent;
      try
      {
         parent = processes.Open(request.ParentProcessId);
      }
      catch(ArgumentException)
      {
         return;
      }
      using(parent)
      {
         if(parent.HasExited)
         {
            return;
         }
         string? path;
         long started;
         try
         {
            path = parent.ProgramPath;
            started = parent.StartTicks;
         }
         catch(InvalidOperationException) when(parent.HasExited)
         {
            return;
         }
         if(started != request.ParentStartTicks || !string.Equals(path, places.InstalledCompanion, StringComparison.OrdinalIgnoreCase))
         {
            throw new InvalidOperationException("The expected companion process identity changed. No installer was started.");
         }
         using CancellationTokenSource timeout = new(CompanionExitLimit, places.Clock);
         try
         {
            await parent.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
         }
         catch(OperationCanceledException exception)
         {
            throw new TimeoutException("The companion did not close within 30 seconds. Close it and retry the update; no process was terminated.", exception);
         }
      }
   }



   /// <summary>Checks the installed payload after MSI success without assuming rollback on mismatch.</summary>
   internal void ValidateInstalledVersions(string requestedVersion, Func<string, string?> readProductVersion)
   {
      string? installedBuild = null;
      foreach(string executable in new[] { places.InstalledCompanion, places.InstalledService, Path.Combine(places.InstalledCli, "qqq-print-agent.exe") })
      {
         string? version;
         try
         {
            version = readProductVersion(executable);
         }
         catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
         {
            throw new InvalidOperationException("Windows Installer returned success, but the installed " + Path.GetFileName(executable) + " version could not be verified. Inspect the installation log and repair the package.", exception);
         }
         if(!MsiUpdateInstaller.InstalledVersionMatches(version, requestedVersion))
         {
            throw new InvalidOperationException("Windows Installer returned success, but the installed " + Path.GetFileName(executable) + " version does not match the requested release. Inspect the installation log and repair the package.");
         }
         if(installedBuild is not null && !string.Equals(version, installedBuild, StringComparison.Ordinal))
         {
            throw new InvalidOperationException("Windows Installer returned success, but the installed " + Path.GetFileName(executable) + " build version is incoherent with the companion. Inspect the installation log and repair the package.");
         }
         installedBuild ??= version;
      }
   }



   /// <summary>
   /// Finishes an update attempt in three independent best-effort steps, so a failure in one never blocks the next: record the
   /// outcome and log it, tell the service to resume printing after the maintenance pause, and reopen the installed companion.
   /// The installer's own log remains the evidence if any step fails.
   /// </summary>
   private async Task CompleteAsync(string version, int code, string message)
   {
      try
      {
         WriteOutcome(new InstallOutcome(places.Clock.GetUtcNow(), version, code, message));
         IActivityLog log = places.OpenLog(places.StateDirectory);
         if(code is 0 or 3010)
         {
            log.Info("[Update] " + message);
         }
         else
         {
            log.Error("[Update] " + message);
         }
      }
      catch(Exception)
      {
         // The independently preserved MSI install.log remains diagnostic evidence.
      }
      try
      {
         await places.Send(new PipeRequest("resume-after-update")).ConfigureAwait(false);
      }
      catch(Exception)
      {
         // An abandoned maintenance pause also expires in the service itself.
      }
      try
      {
         ValidateInstalledPath(places.InstalledCompanion);
         using IRunningProcess? companion = processes.Start(new ProcessStartInfo(places.InstalledCompanion) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(places.InstalledCompanion)! });
      }
      catch(Exception)
      {
         // Operator can read the protected outcome/log and open the installed app manually.
      }
   }



   /// <summary>Saves the last update outcome by writing a temporary file in the protected folder and moving it over the previous result, so a reader never sees a half-written file.</summary>
   private void WriteOutcome(InstallOutcome outcome)
   {
      ValidateStagingDirectory(UpdatesDirectory);
      Directory.CreateDirectory(UpdatesDirectory);
      string path = Path.Combine(UpdatesDirectory, "last-result.json");
      MsiUpdateInstaller.ValidateNoReparseComponents(path);
      string temporary = Path.Combine(UpdatesDirectory, "result-" + Guid.NewGuid().ToString("N") + ".tmp");
      try
      {
         using(FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
         {
            JsonSerializer.Serialize(stream, outcome, PrintApiClient.JsonOptions);
            stream.Flush(flushToDisk: true);
         }
         File.Move(temporary, path, overwrite: true);
      }
      finally { File.Delete(temporary); }
   }



   /// <summary>Read-only, bounded last outcome; retained for Settings/log diagnostics rather than consumed.</summary>
   internal string? ReadLastOutcomeMessage()
   {
      try
      {
         string path = Path.Combine(UpdatesDirectory, "last-result.json");
         if(!File.Exists(path))
         {
            return null;
         }
         ValidateStagedFile(path);
         if(new FileInfo(path).Length > MaximumRequestBytes)
         {
            return "The previous update outcome is invalid. Inspect the protected update logs.";
         }
         using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
         InstallOutcome? result = JsonSerializer.Deserialize<InstallOutcome>(stream, PrintApiClient.JsonOptions);
         return result is null ? null : result.CompletedAt.ToString("u", CultureInfo.InvariantCulture) + " — " + result.Message;
      }
      catch(Exception)
      {
         return "The previous update outcome could not be read. Inspect the protected update logs.";
      }
   }



   /// <summary>The recorded result of one update attempt: when it finished, which release it targeted, the installer's exit code and the message shown to the operator.</summary>
   private sealed record InstallOutcome(DateTimeOffset CompletedAt, string Version, int ExitCode, string Message);
}
