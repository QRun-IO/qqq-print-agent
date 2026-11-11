/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using KofTwentyTwo.AppKit.Logging;
using KofTwentyTwo.AppKit.Updates;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// AppKit update flow backed by a coherent machine MSI rather than a per-user executable update. The release feed, the
/// service's pipe and the update steps are given to it, so the whole flow (check, download, pause, hand-off, and what
/// is undone when a step fails) can be run against fakes; the companion uses the public constructor, which binds the
/// GitHub release feed, the installed service's pipe and the installed product's update steps.
/// </summary>
public sealed class MsiUpdateService : IUpdateService, IDisposable
{
   /// <summary>Closes the companion once the update has been handed to the helper, so the installer can replace its files.</summary>
   private readonly Action _exitApplication;



   /// <summary>The companion's activity log.</summary>
   private readonly IActivityLog _log;



   /// <summary>Where releases are found and their installers downloaded from.</summary>
   private readonly IMsiReleaseSource _releases;



   /// <summary>Sends one request to the service and returns its reply.</summary>
   private readonly Func<PipeRequest, Task<string>> _send;



   /// <summary>The update steps: the staging checks, the preparation and the hand-off to the helper.</summary>
   private readonly MsiUpdateSteps _installer;



   /// <summary>The release the last successful check found, which is the only one an install will take.</summary>
   private MsiRelease? _pending;



   /// <summary>The update service of the installed companion: the GitHub release feed for the given asset prefix, the installed service's pipe and the update steps of the installed product.</summary>
   public MsiUpdateService(Action exitApplication, IActivityLog log, string assetPrefix = Qqq.PrintAgent.Core.Edition.DefaultAssetPrefix)
      : this(exitApplication, log, new GitHubMsiReleaseClient(assetPrefix: assetPrefix), AgentPipeClient.SendAsync, MsiUpdateInstaller.Installed)
   {
   }



   /// <summary>The update service over a given release source, pipe and update steps.</summary>
   internal MsiUpdateService(Action exitApplication, IActivityLog log, IMsiReleaseSource releases, Func<PipeRequest, Task<string>> send, MsiUpdateSteps installer)
   {
      _exitApplication = exitApplication;
      _log = log;
      _releases = releases;
      _send = send;
      _installer = installer;
   }



   /// <summary>How this update service reaches the print service, so that a test can see which pipe the companion's constructor bound.</summary>
   internal Func<PipeRequest, Task<string>> Send => _send;



   /// <summary>The update steps this update service uses, so that a test can see which steps the companion's constructor bound.</summary>
   internal MsiUpdateSteps Steps => _installer;



   /// <summary>Raised with each step of an update as it begins and with the download's progress, for the companion to show.</summary>
   public event EventHandler<UpdateStatusEventArgs>? StatusChanged;



   /// <summary>The version of this build, as stamped into the assembly; it is what the release feed's versions are compared with.</summary>
   public string? CurrentVersion { get; } = typeof(MsiUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;



   /// <summary>
   /// True only for the MSI-installed companion (running from its Program Files path with the machine state folder present).
   /// A development or portable copy cannot replace the service and CLI together, so it is never offered an in-app update.
   /// </summary>
   public bool IsSupported => _installer.RunsAsInstalledCompanion;



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
         // The service finishes the jobs it has already claimed before it answers, or answers that it is not ready.
         paused = true;
         StatusChanged?.Invoke(this, new UpdateStatusEventArgs("Waiting for active printing to finish…"));
         string response = await _send(new PipeRequest("prepare-update")).ConfigureAwait(false);
         if(!string.Equals(response, "Ready for update.", StringComparison.Ordinal))
         {
            throw new InvalidOperationException(PreparationNotCompleted(response));
         }
         cancellationToken.ThrowIfCancellationRequested();
         _installer.StartHelper(request);
         handedOff = true;
         _log.Info("[Companion] Verified update handed to Windows Installer; closing the companion.");
         _exitApplication();
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
      string staging = Path.Combine(_installer.UpdatesDirectory, Guid.NewGuid().ToString("N"));
      _installer.ValidateStagingDirectory(staging);
      Directory.CreateDirectory(staging);
      _installer.ValidateStagingDirectory(staging);
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
         return await Task.Run(() => _installer.PrepareHelper(package, release.Version, _installer.CurrentProcessId), token).ConfigureAwait(false);
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
         _installer.ValidateStagingDirectory(directory);
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
         _log.Warning("[Companion] Unapplied update staging could not be removed: " + Describe(exception));
      }
   }



   /// <summary>Tells the service to lift the maintenance pause after an update was abandoned; best effort, since the service also expires an abandoned pause on its own.</summary>
   private async Task ResumePrintingAsync()
   {
      try
      {
         await _send(new PipeRequest("resume-after-update")).ConfigureAwait(false);
      }
      catch(Exception)
      {
         // The service also expires an abandoned update pause automatically.
      }
   }



   /// <summary>
   /// Words an update preparation the service did not complete. "Not ready for update: ..." is the service saying that work
   /// it claimed before the pause is still running, and "Update preparation was interrupted: ..." that printing was resumed
   /// or a sign-in began while it waited. Each of those sentences already says what happened and what to do, so it is
   /// shown as it is, after one sentence saying that nothing was installed. Any other answer is an error, or something this
   /// build does not know, and is shown as that: it is not an invitation to wait for printing that may have nothing to do
   /// with it.
   /// </summary>
   internal static string PreparationNotCompleted(string response)
      => response.StartsWith("Not ready for update:", StringComparison.Ordinal) || response.StartsWith("Update preparation was interrupted:", StringComparison.Ordinal)
         ? "The update was not started, and the current version remains installed. " + response
         : "The service could not prepare for an update, and the current version remains installed. The service answered: " + response;



   /// <summary>Turns a failure into a message for the operator: a cancellation reads as "canceled, current version remains", anything else is redacted so no secret or path detail leaks into the UI.</summary>
   private static string Describe(Exception exception) => exception is OperationCanceledException
      ? "The update was canceled. The current version remains installed."
      : AgentActivityLog.Redact(exception.Message);



   /// <summary>Releases the release source.</summary>
   public void Dispose() => _releases.Dispose();
}
