/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Where the update service finds a newer release and gets its installer from. The installed product uses
/// <see cref="GitHubMsiReleaseClient"/>, which reads the fixed release feed and verifies what it downloads; a test
/// passes a source of its own, so the update flow can be run without the network. Which release is eligible and
/// whether the downloaded bytes are the release's is decided by the source; what happens before and after is decided
/// by <see cref="MsiUpdateService"/>.
/// </summary>
internal interface IMsiReleaseSource : IDisposable
{
   /// <summary>The newest eligible release that is newer than the given version, or null when there is none.</summary>
   Task<MsiRelease?> FindUpdateAsync(string currentVersion, CancellationToken cancellationToken = default);



   /// <summary>
   /// Downloads the release's installer into the given staging folder, verifies it, and returns the path of the verified
   /// file, reporting progress as a fraction of the whole. A download that fails leaves no installer behind.
   /// </summary>
   Task<string> DownloadVerifiedAsync(MsiRelease release, string currentVersion, string stagingDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}
