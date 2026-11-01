/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Applying part of the updater, which runs only inside the staged helper copy. It re-verifies the request, waits for the
/// companion that asked to close, installs the verified MSI with Windows Installer, checks that all three installed programs
/// carry the requested release, records the outcome, and reopens the companion. It never kills a process or an installer.
/// </summary>
public static partial class MsiUpdateInstaller
{
   /// <summary>Runs only inside the protected copied helper; never kills an installer or an application.</summary>
   public static async Task<int> ApplyAsync(string requestPath)
   {
      int code = 1;
      string message;
      string version = "unknown";
      try
      {
         RequireAdministrator();
         InstallRequest request = ReadRequest(requestPath);
         version = request.Version;
         string directory = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
         string helper = Path.Combine(directory, "helper", "qqq-print-agent.exe");
         if(!string.Equals(Environment.ProcessPath, helper, StringComparison.OrdinalIgnoreCase))
         {
            throw new InvalidOperationException("Apply must run from the verified staged helper copy.");
         }
         ValidateStagedFile(helper);
         await WaitForCompanionExitAsync(request).ConfigureAwait(false);
         // Retain this non-writable/non-deletable handle until Windows Installer exits.
         using FileStream packageLock = OpenVerifiedPackage(request.PackagePath, request.Sha256);
         string installLog = Path.Combine(directory, "install.log");
         ValidateNoReparseComponents(installLog);
         ProcessStartInfo start = CreateInstallerStart(request.PackagePath, installLog);
         using Process installer = Process.Start(start) ?? throw new InvalidOperationException("Windows Installer could not start.");
         await installer.WaitForExitAsync().ConfigureAwait(false);
         code = installer.ExitCode;
         if(code == 0)
         {
            ValidateInstalledVersions(request.Version, path => FileVersionInfo.GetVersionInfo(path).ProductVersion);
         }
         message = DescribeExitCode(code);
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
   private static async Task WaitForCompanionExitAsync(InstallRequest request)
   {
      Process parent;
      try
      {
         parent = Process.GetProcessById(request.ParentProcessId);
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
            path = parent.MainModule?.FileName;
            started = parent.StartTime.ToUniversalTime().Ticks;
         }
         catch(InvalidOperationException) when(parent.HasExited)
         {
            return;
         }
         if(started != request.ParentStartTicks || !string.Equals(path, InstalledCompanion, StringComparison.OrdinalIgnoreCase))
         {
            throw new InvalidOperationException("The expected companion process identity changed. No installer was started.");
         }
         using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
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



   /// <summary>
   /// Opens the staged MSI for reading with sharing that blocks writes and deletes, and checks its size and SHA-256 against the
   /// request in constant time. The caller keeps the handle open until the installer exits, so the file cannot be swapped
   /// between the check and the install.
   /// </summary>
   internal static FileStream OpenVerifiedPackage(string path, string expectedHash)
   {
      FileStream content = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      try
      {
         if(content.Length is <= 0 or > 256L * 1024 * 1024
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(content), Convert.FromHexString(expectedHash)))
         {
            throw new InvalidDataException("The staged update changed or failed its checksum. Download it again.");
         }
         return content;
      }
      catch
      {
         content.Dispose();
         throw;
      }
   }



   /// <summary>Builds the msiexec start: the system msiexec, passive display, no automatic restart and a verbose log in the protected folder. Arguments go in a list, never a shell string.</summary>
   internal static ProcessStartInfo CreateInstallerStart(string package, string logPath)
   {
      ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "msiexec.exe")) { UseShellExecute = false, CreateNoWindow = true };
      foreach(string argument in new[] { "/i", package, "/passive", "/norestart", "/L*v", logPath })
      {
         start.ArgumentList.Add(argument);
      }
      return start;
   }



   /// <summary>
   /// Turns an installer exit code into an honest message for the operator: success, restart required (3010, which is not
   /// full success), operator cancellation (1602), or an unreported failure that points at the private log.
   /// </summary>
   internal static string DescribeExitCode(int code) => code switch
   {
      0 => "Update installed successfully. Open the companion to verify service status.",
      3010 => "Update installed, but Windows requires a restart to finish replacing files. Restart Windows before treating the new version as fully active.",
      1602 => "Windows Installer canceled the update. The previous installation may remain; inspect the version and service status before retrying.",
      _ => "Windows Installer did not report a completed update (exit " + code.ToString(CultureInfo.InvariantCulture) + "). Inspect the private installation log and service status before retrying.",
   };



   /// <summary>True when the installed product version, ignoring the build metadata after "+", is exactly the requested release.</summary>
   internal static bool InstalledVersionMatches(string? productVersion, string requestedVersion)
      => productVersion is not null && string.Equals(productVersion.Split('+')[0], requestedVersion, StringComparison.Ordinal);



   /// <summary>Checks the installed payload after MSI success without assuming rollback on mismatch.</summary>
   internal static void ValidateInstalledVersions(string requestedVersion, Func<string, string?> readProductVersion)
   {
      string? installedBuild = null;
      foreach(string executable in new[]
      {
         InstalledCompanion,
         Path.Combine(InstalledRoot, "Service", "Qqq.PrintAgent.Service.exe"),
         Path.Combine(InstalledCli, "qqq-print-agent.exe"),
      })
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
         if(!InstalledVersionMatches(version, requestedVersion))
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
   private static async Task CompleteAsync(string version, int code, string message)
   {
      try
      {
         WriteOutcome(new InstallOutcome(DateTimeOffset.UtcNow, version, code, message));
         IActivityLog log = AgentActivityLog.Create(AgentStore.DefaultDirectory);
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
         await AgentPipeClient.SendAsync(new PipeRequest("resume-after-update")).ConfigureAwait(false);
      }
      catch(Exception)
      {
         // An abandoned maintenance pause also expires in the service itself.
      }
      try
      {
         ValidateInstalledPath(InstalledCompanion);
         using var companion = Process.Start(new ProcessStartInfo(InstalledCompanion) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(InstalledCompanion)! });
      }
      catch(Exception)
      {
         // Operator can read the protected outcome/log and open the installed app manually.
      }
   }



   /// <summary>Saves the last update outcome by writing a temporary file in the protected folder and moving it over the previous result, so a reader never sees a half-written file.</summary>
   private static void WriteOutcome(InstallOutcome outcome)
   {
      ValidateStagingDirectory(UpdatesDirectory);
      Directory.CreateDirectory(UpdatesDirectory);
      string path = Path.Combine(UpdatesDirectory, "last-result.json");
      ValidateNoReparseComponents(path);
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
   public static string? ReadLastOutcomeMessage()
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
