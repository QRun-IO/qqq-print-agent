/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A release source that exists only in memory: it offers the release a test gives it and "downloads" by writing the
/// bytes the test chose into the staging folder it is handed, under a new name each time, as the real client does. It
/// can be told to fail a check or a download, and it honours cancellation the way the real one does, by ending with
/// the cancellation. Nothing reaches the network.
/// </summary>
internal sealed class FakeMsiReleaseSource : IMsiReleaseSource
{
   /// <summary>The release a check finds; null when the installed version is the newest.</summary>
   internal MsiRelease? Offered { get; set; }



   /// <summary>When set, a check fails with this.</summary>
   internal Exception? CheckFailure { get; set; }



   /// <summary>The installed version each check was made for, in order.</summary>
   internal List<string> Checks { get; } = [];



   /// <summary>When set, a download fails with this after the staging folder has been made.</summary>
   internal Exception? DownloadFailure { get; set; }



   /// <summary>What a downloaded installer holds.</summary>
   internal byte[] Content { get; set; } = [0xD0, 0xCF, 0x11, 0xE0, 9, 8, 7];



   /// <summary>The fractions of the whole a download reports as it goes.</summary>
   internal double[] Progress { get; set; } = [0.5, 1.0];



   /// <summary>Every download asked for, in order: the release, the installed version it was asked for and the staging folder.</summary>
   internal List<(MsiRelease Release, string CurrentVersion, string Directory)> Downloads { get; } = [];



   /// <summary>Called during a download with the staging folder, so a test can put something there or cancel while the download is under way.</summary>
   internal Action<string>? WhileDownloading { get; set; }



   /// <summary>True once the source has been released.</summary>
   internal bool Disposed { get; private set; }



   /// <summary>Notes the check and answers with the offered release, or fails as the test asked.</summary>
   public Task<MsiRelease?> FindUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
   {
      Checks.Add(currentVersion);
      cancellationToken.ThrowIfCancellationRequested();
      return CheckFailure is not null ? Task.FromException<MsiRelease?>(CheckFailure) : Task.FromResult(Offered);
   }



   /// <summary>Notes the download, makes the staging folder, writes the installer into it under a new name and reports progress; or fails as the test asked, leaving no installer.</summary>
   public Task<string> DownloadVerifiedAsync(MsiRelease release, string currentVersion, string stagingDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
   {
      Downloads.Add((release, currentVersion, stagingDirectory));
      Directory.CreateDirectory(stagingDirectory);
      WhileDownloading?.Invoke(stagingDirectory);
      cancellationToken.ThrowIfCancellationRequested();
      if(DownloadFailure is not null)
      { return Task.FromException<string>(DownloadFailure); }
      string package = Path.Combine(stagingDirectory, "QqqPrintAgent-v" + release.Version + "-x64-" + Guid.NewGuid().ToString("N") + ".msi");
      File.WriteAllBytes(package, Content);
      foreach(double fraction in Progress)
      { progress?.Report(fraction); }
      return Task.FromResult(package);
   }



   /// <summary>Notes that the source was released.</summary>
   public void Dispose() => Disposed = true;
}
