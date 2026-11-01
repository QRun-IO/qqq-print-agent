/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using KofTwentyTwo.AppKit.Logging;
using KofTwentyTwo.AppKit.Updates;

namespace Qqq.PrintAgent.Windows;

/// <summary>AppKit update flow backed by a coherent machine MSI rather than a per-user executable update.</summary>
public sealed class MsiUpdateService(Action exitApplication, IActivityLog log) : IUpdateService, IDisposable
{
   private readonly GitHubMsiReleaseClient _releases = new();
   private MsiRelease? _pending;

   public event EventHandler<UpdateStatusEventArgs>? StatusChanged;

   public string? CurrentVersion { get; } = typeof(MsiUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;



   /// <summary>
   /// True only for the MSI-installed companion (running from its Program Files path with the machine state folder present).
   /// A development or portable copy cannot replace the service and CLI together, so it is never offered an in-app update.
   /// </summary>
   public bool IsSupported
   {
      get
      {
         string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");
         return string.Equals(Environment.ProcessPath, installed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(AgentStore.DefaultDirectory);
      }
   }



   /// <summary>
   /// Asks the release feed whether a newer eligible release exists and remembers it for the later install step. Any previous
   /// candidate is cleared first, so a failed check can never leave an old release to be installed. A failure is returned
   /// as a message for the user, not thrown.
   /// </summary>
   public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
   {
      _pending = null;
      if(!IsSupported)
      {
         return UpdateCheckResult.Failed("Install the machine MSI to check for updates from this app.");
      }
      try
      {
         _pending = await _releases.FindUpdateAsync(CurrentVersion ?? "", cancellationToken).ConfigureAwait(false);
         return _pending is null ? UpdateCheckResult.UpToDate : UpdateCheckResult.Available(_pending.Version);
      }
      catch(Exception exception)
      {
         return UpdateCheckResult.Failed(Describe(exception));
      }
   }



   /// <summary>
   /// Stages the checked release, asks the service to finish active printing and pause, starts the staged helper and closes
   /// the companion so the installer can replace its files. Returns null on a successful hand-off or a message otherwise.
   /// If anything fails before the hand-off the pause is lifted and the staged files removed, and once the helper has started
   /// the failure is reported as "could not close", never as a failed install.
   /// </summary>
   public async Task<string?> DownloadAndApplyAsync(IProgress<int>? progress = null, CancellationToken cancellationToken = default)
   {
      bool paused = false;
      bool handedOff = false;
      string? request = null;
      try
      {
         if(!IsSupported || _pending is not MsiRelease release)
         {
            return "Check for updates from the installed app before installing an update.";
         }
         request = await StageAsync(release, progress, cancellationToken).ConfigureAwait(false);
         cancellationToken.ThrowIfCancellationRequested();
         // The service finishes its current poll/submission before acknowledging this pause.
         paused = true;
         StatusChanged?.Invoke(this, new UpdateStatusEventArgs("Waiting for active printing to finish…"));
         string response = await AgentPipeClient.SendAsync(new PipeRequest("prepare-update")).ConfigureAwait(false);
         if(!string.Equals(response, "Ready for update.", StringComparison.Ordinal))
         {
            throw new InvalidOperationException("The service could not prepare for an update. Let active printing finish and try again. " + response);
         }
         cancellationToken.ThrowIfCancellationRequested();
         MsiUpdateInstaller.StartHelper(request);
         handedOff = true;
         log.Info("[Companion] Verified update handed to Windows Installer; closing the companion.");
         exitApplication();
         return null;
      }
      catch(Exception exception)
      {
         if(handedOff)
         {
            return "The installer helper started, but the companion could not close. Check the update outcome and Logs before trying again.";
         }
         if(paused)
         {
            await ResumePrintingAsync().ConfigureAwait(false);
         }
         if(request is not null)
         {
            RemoveUnappliedStage(Path.GetDirectoryName(request)!);
         }
         return Describe(exception);
      }
   }



   /// <summary>
   /// Downloads the verified MSI into a fresh protected staging folder (reporting percent progress) and prepares the helper
   /// request, returning its path. The folder name is a new GUID and is validated before and after creation, and a failure at
   /// any step removes what was staged.
   /// </summary>
   private async Task<string> StageAsync(MsiRelease release, IProgress<int>? progress, CancellationToken token)
   {
      string staging = Path.Combine(MsiUpdateInstaller.UpdatesDirectory, Guid.NewGuid().ToString("N"));
      MsiUpdateInstaller.ValidateStagingDirectory(staging);
      Directory.CreateDirectory(staging);
      MsiUpdateInstaller.ValidateStagingDirectory(staging);
      StatusChanged?.Invoke(this, new UpdateStatusEventArgs("Downloading the update…"));
      IProgress<double> downloadProgress = new Progress<double>(value =>
      {
         int percent = (int)Math.Clamp(value * 100, 0, 100);
         progress?.Report(percent);
         StatusChanged?.Invoke(this, new UpdateStatusEventArgs($"Downloading the update: {percent}%"));
      });
      try
      {
         string package = await _releases.DownloadVerifiedAsync(release, CurrentVersion ?? "", staging, downloadProgress, token).ConfigureAwait(false);
         StatusChanged?.Invoke(this, new UpdateStatusEventArgs("Update verified. Preparing Windows Installer…"));
         return await Task.Run(() => MsiUpdateInstaller.PrepareHelper(package, release.Version, Environment.ProcessId), token).ConfigureAwait(false);
      }
      catch
      {
         RemoveUnappliedStage(staging);
         throw;
      }
   }



   /// <summary>
   /// Deletes a staging folder that was never applied. It first walks the whole tree and refuses to delete anything
   /// containing a link or junction, because a recursive delete could otherwise follow it out of the folder; a refusal or
   /// failure is only logged, and the folder is retained for an administrator.
   /// </summary>
   private void RemoveUnappliedStage(string directory)
   {
      try
      {
         MsiUpdateInstaller.ValidateStagingDirectory(directory);
         Stack<string> pending = new();
         pending.Push(directory);
         while(pending.TryPop(out string? current))
         {
            foreach(string path in Directory.EnumerateFileSystemEntries(current))
            {
               FileAttributes attributes = File.GetAttributes(path);
               if(attributes.HasFlag(FileAttributes.ReparsePoint))
               {
                  throw new IOException("Unapplied update staging contains a link and was retained.");
               }
               if(attributes.HasFlag(FileAttributes.Directory))
               {
                  pending.Push(path);
               }
            }
         }
         Directory.Delete(directory, recursive: true);
      }
      catch(Exception exception)
      {
         log.Warning("[Companion] Unapplied update staging could not be removed: " + Describe(exception));
      }
   }



   /// <summary>Tells the service to lift the maintenance pause after an update was abandoned; best effort, since the service also expires an abandoned pause on its own.</summary>
   private static async Task ResumePrintingAsync()
   {
      try
      {
         await AgentPipeClient.SendAsync(new PipeRequest("resume-after-update")).ConfigureAwait(false);
      }
      catch(Exception)
      {
         // The service also expires an abandoned update pause automatically.
      }
   }



   /// <summary>Turns a failure into a message for the operator: a cancellation reads as "canceled, current version remains", anything else is redacted so no secret or path detail leaks into the UI.</summary>
   private static string Describe(Exception exception) => exception is OperationCanceledException
      ? "The update was canceled. The current version remains installed."
      : AgentActivityLog.Redact(exception.Message);



   /// <summary>Releases the release-feed client.</summary>
   public void Dispose() => _releases.Dispose();
}
