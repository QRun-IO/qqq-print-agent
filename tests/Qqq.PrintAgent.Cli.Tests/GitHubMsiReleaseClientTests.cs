/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The update client discovers and downloads releases from GitHub and the machine will run what it fetches, so these tests hold
/// each trust boundary: which release counts as an update, which URLs and redirects are followed, and that only a checksum-verified
/// file ever remains on disk. A fake HTTP handler stands in for GitHub; nothing touches the network.
/// </summary>
public sealed class GitHubMsiReleaseClientTests
{
   private const string Version = "0.1.3";
   private static readonly byte[] Content = [1, 2, 3, 4];



   /// <summary>
   /// Among drafts, older, prerelease and stable tags, the client picks the newest version on the installed channel: a release
   /// candidate follows release candidates (rc.10 beats rc.2 numerically, not as text) and a build-metadata suffix is ignored.
   /// Drafts are never offered, and exactly one metadata request is made.
   /// </summary>
   [Theory]
   [InlineData("0.1.2", "0.1.3")]
   [InlineData("0.1.2-rc.1", "0.1.4-rc.10")]
   [InlineData("v0.1.3-rc.2+synthetic.commit", "0.1.4-rc.10")]
   public async Task SelectsNewestEligibleVersionOnInstalledChannel(string installed, string expected)
   {
      object[] releases = [Release("0.1.1"), Release("0.1.3-rc.2"), Release("0.1.3"), Release("0.1.4-rc.10"), Release("0.1.4-rc.2"), Release("0.2.0", draft: true)];
      using Handler handler = new(_ => Json(releases));
      using GitHubMsiReleaseClient client = new(handler);
      MsiRelease? actual = await client.FindUpdateAsync(installed);
      Assert.Equal(expected, actual?.Version);
      Assert.Equal("https://api.github.com/repos/QRun-IO/qqq-print-agent/releases?per_page=100", Assert.Single(handler.Requests));
   }



   /// <summary>A stable 0.1.3 is newer than any 0.1.3 release candidate, so a machine on rc.9 is offered the stable build even though rc.10 exists.</summary>
   [Fact]
   public async Task StableReleaseSupersedesSameVersionCandidate()
   {
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { Release("0.1.3-rc.10"), Release("0.1.3") })));
      Assert.Equal("0.1.3", (await client.FindUpdateAsync("0.1.3-rc.9"))?.Version);
   }



   /// <summary>An installed version equal to or newer than every release produces no offer, so the updater never downgrades or reinstalls the same build.</summary>
   [Theory]
   [InlineData("0.1.3")]
   [InlineData("1.0.0")]
   public async Task DoesNotOfferCurrentOrOlderRelease(string installed)
   {
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { Release("0.1.3"), Release("0.1.2"), Release("0.1.1") })));
      Assert.Null(await client.FindUpdateAsync(installed));
   }



   /// <summary>Releases 0.1.1 and 0.1.2 (including its candidate) are withheld even from a 0.1.0 install, because they are known broken builds.</summary>
   [Fact]
   public async Task DoesNotOfferKnownBrokenLegacyVersions()
   {
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { Release("0.1.1"), Release("0.1.2-rc.1"), Release("0.1.2") })));
      Assert.Null(await client.FindUpdateAsync("0.1.0"));
   }



   /// <summary>Tags with leading zeros, an unsupported prerelease label or a path separator are ignored, so only strictly formed release numbers can be offered or reach a download URL.</summary>
   [Theory]
   [InlineData("0.1.03")]
   [InlineData("0.1.3-beta.1")]
   [InlineData("0.1.3/other")]
   [InlineData("0.1.3-rc.01")]
   public async Task IgnoresUnsupportedOrMalformedReleaseTags(string version)
   {
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { Release(version) })));
      Assert.Null(await client.FindUpdateAsync("0.1.2-rc.1"));
   }



   /// <summary>A stable-looking tag marked as a prerelease is contradictory metadata and is rejected outright rather than guessed at.</summary>
   [Fact]
   public async Task RejectsVersionAndPrereleaseFlagMismatch()
   {
      object invalid = new { draft = false, tag_name = "v0.1.3", prerelease = true, assets = Array.Empty<object>() };
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { invalid })));
      await Assert.ThrowsAsync<InvalidDataException>(() => client.FindUpdateAsync("0.1.2-rc.1"));
   }



   /// <summary>Two assets with the same expected installer name make the intended file ambiguous, so the release is rejected instead of choosing one.</summary>
   [Fact]
   public async Task RejectsDuplicateExpectedReleaseAssets()
   {
      object invalid = new { draft = false, tag_name = "v0.1.3", prerelease = false, assets = new[] { Asset(Version, Name(Version), 4), Asset(Version, Name(Version), 4), Asset(Version, "SHA256SUMS", 100) } };
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { invalid })));
      await Assert.ThrowsAsync<InvalidDataException>(() => client.FindUpdateAsync("0.1.2"));
   }



   /// <summary>
   /// An installer URL must be https on github.com, in this repository's download path for the same version, with no
   /// dot-segments, query string or user info. Any deviation is refused before a single request is sent.
   /// </summary>
   [Theory]
   [InlineData("http://github.com/QRun-IO/qqq-print-agent/releases/download/v0.1.3/QqqPrintAgent-v0.1.3-x64.msi")]
   [InlineData("https://github.com/other/project/releases/download/v0.1.3/QqqPrintAgent-v0.1.3-x64.msi")]
   [InlineData("https://github.com/QRun-IO/qqq-print-agent/releases/download/v0.1.4/QqqPrintAgent-v0.1.3-x64.msi")]
   [InlineData("https://github.com/QRun-IO/qqq-print-agent/releases/download/v0.1.3/../v0.1.3/QqqPrintAgent-v0.1.3-x64.msi")]
   [InlineData("https://github.com/QRun-IO/qqq-print-agent/releases/download/v0.1.3/QqqPrintAgent-v0.1.3-x64.msi?other=1")]
   [InlineData("https://name@github.com/QRun-IO/qqq-print-agent/releases/download/v0.1.3/QqqPrintAgent-v0.1.3-x64.msi")]
   public async Task RejectsUnsafeOrMismatchedAssetBeforeAnyDownload(string url)
   {
      using Handler handler = new(_ => throw new InvalidOperationException("No request should be sent."));
      using GitHubMsiReleaseClient client = new(handler);
      MsiRelease release = ValidRelease() with { MsiUrl = new Uri(url) };
      await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadVerifiedAsync(release, "0.1.2", Path.GetTempPath()));
      Assert.Empty(handler.Requests);
   }



   /// <summary>The download call re-checks eligibility itself (same, older, cross-channel and legacy targets are refused with no request), so a caller holding a stale or hand-built release cannot bypass the update rules.</summary>
   [Theory]
   [InlineData("0.1.3", "0.1.3")]
   [InlineData("0.1.4", "0.1.3")]
   [InlineData("0.1.2", "0.1.3-rc.1")]
   [InlineData("0.1.0-rc.1", "0.1.2")]
   public async Task RevalidatesVersionAndChannelBeforeDownload(string installed, string target)
   {
      using Handler handler = new(_ => throw new InvalidOperationException("No request should be sent."));
      using GitHubMsiReleaseClient client = new(handler);
      await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadVerifiedAsync(ValidRelease(target), installed, Path.GetTempPath()));
      Assert.Empty(handler.Requests);
   }



   /// <summary>A verified download is written to a new unique .msi in the staging folder each time, with the exact bytes and no leftover .partial file.</summary>
   [Fact]
   public async Task StagesOnlyChecksumVerifiedMsiToUniqueFiles()
   {
      string directory = TemporaryDirectory();
      try
      {
         using GitHubMsiReleaseClient client = DownloadClient(Checksums());
         string first = await client.DownloadVerifiedAsync(ValidRelease(), "0.1.2", directory);
         string second = await client.DownloadVerifiedAsync(ValidRelease(), "0.1.2", directory);
         Assert.NotEqual(first, second, StringComparer.Ordinal);
         Assert.Equal(directory, Path.GetDirectoryName(first));
         Assert.EndsWith(".msi", first, StringComparison.Ordinal);
         Assert.Equal(Content, await File.ReadAllBytesAsync(first));
         Assert.Equal(2, Directory.GetFiles(directory).Length);
         Assert.Empty(Directory.GetFiles(directory, "*.partial"));
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>
   /// A checksum file that repeats the entry, is malformed, lists a path outside the folder, lacks the installer or holds the
   /// wrong hash fails the download and leaves the staging folder empty, so an unverified file can never be installed.
   /// </summary>
   [Theory]
   [InlineData("duplicate")]
   [InlineData("malformed")]
   [InlineData("traversal")]
   [InlineData("missing")]
   [InlineData("wrongHash")]
   public async Task RejectsInvalidChecksumsWithoutLeavingDownloads(string failure)
   {
      string directory = TemporaryDirectory();
      string sums = failure switch
      {
         "duplicate" => Checksums() + Checksums(),
         "malformed" => "not a checksum\n",
         "traversal" => Checksums() + new string('0', 64) + "  ../other.msi\n",
         "missing" => new string('0', 64) + "  another.msi\n",
         _ => new string('0', 64) + "  " + Name(Version) + "\n",
      };
      try
      {
         using GitHubMsiReleaseClient client = DownloadClient(sums);
         await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadVerifiedAsync(ValidRelease(), "0.1.2", directory));
         Assert.Empty(Directory.GetFiles(directory));
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>
   /// Cancelling mid-write deletes the download's own .partial file but leaves an unrelated file already in the folder, so
   /// cleanup cannot remove anything the updater did not create. The progress callback cancels at the moment the partial exists.
   /// </summary>
   [Fact]
   public async Task CancellationAfterWritingRemovesOnlyItsOwnPartialFile()
   {
      string directory = TemporaryDirectory();
      string sentinel = Path.Combine(directory, "keep.txt");
      await File.WriteAllTextAsync(sentinel, "synthetic unrelated file");
      using CancellationTokenSource stop = new();
      bool sawPartial = false;
      InlineProgress progress = new(_ =>
      {
         sawPartial = Directory.GetFiles(directory, "*.partial").Length == 1;
         stop.Cancel();
      });
      try
      {
         using GitHubMsiReleaseClient client = DownloadClient(Checksums());
         await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadVerifiedAsync(ValidRelease(), "0.1.2", directory, progress, stop.Token));
         Assert.True(sawPartial);
         Assert.Equal(sentinel, Assert.Single(Directory.GetFiles(directory)));
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>Release metadata over 2 MiB and a declared installer size over 256 MiB are refused up front, bounding memory and disk use whatever the server claims.</summary>
   [Fact]
   public async Task RejectsOversizeMetadataAndDeclaredMsi()
   {
      using GitHubMsiReleaseClient metadata = new(new Handler(_ => Bytes(new byte[2 * 1024 * 1024 + 1])));
      await Assert.ThrowsAsync<InvalidDataException>(() => metadata.FindUpdateAsync("0.1.2"));
      using GitHubMsiReleaseClient download = DownloadClient(Checksums());
      await Assert.ThrowsAsync<InvalidDataException>(() => download.DownloadVerifiedAsync(ValidRelease() with { Size = 256L * 1024 * 1024 + 1 }, "0.1.2", Path.GetTempPath()));
   }



   /// <summary>If the bytes received differ in length from the declared size, the download fails and nothing is left staged.</summary>
   [Fact]
   public async Task RejectsMsiLengthMismatchAndCleansStaging()
   {
      string directory = TemporaryDirectory();
      try
      {
         using GitHubMsiReleaseClient client = DownloadClient(Checksums());
         await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadVerifiedAsync(ValidRelease() with { Size = 5 }, "0.1.2", directory));
         Assert.Empty(Directory.GetFiles(directory));
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>A redirect to another host, to plain http, or to a lookalike subdomain of the asset host is refused when it is received, so only the one real request is made and the target is never contacted.</summary>
   [Theory]
   [InlineData("https://evil.invalid/installer")]
   [InlineData("http://release-assets.githubusercontent.com/installer")]
   [InlineData("https://release-assets.githubusercontent.com.evil.invalid/installer")]
   public async Task RejectsUntrustedRedirectBeforeFollowingIt(string target)
   {
      using Handler handler = new(_ => Redirect(target));
      using GitHubMsiReleaseClient client = new(handler);
      await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadVerifiedAsync(ValidRelease(), "0.1.2", Path.GetTempPath()));
      Assert.Single(handler.Requests);
   }



   /// <summary>
   /// A redirect from github.com to GitHub's own asset host is followed for both the checksum and the installer (four requests in
   /// all), proving normal downloads work. The fake installer response is too short, so the call ends in a length failure with
   /// the folder clean.
   /// </summary>
   [Fact]
   public async Task FollowsOnlyBoundedGithubAssetRedirects()
   {
      string directory = TemporaryDirectory();
      try
      {
         using Handler handler = new(request => request.RequestUri!.Host switch
         {
            "github.com" => Redirect("https://release-assets.githubusercontent.com/asset?synthetic=1"),
            _ => Bytes(Encoding.UTF8.GetBytes(Checksums())),
         });
         using GitHubMsiReleaseClient client = new(handler);
         // The checksum CDN response is accepted; the subsequent MSI response fails length verification.
         await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadVerifiedAsync(ValidRelease(), "0.1.2", directory));
         Assert.Equal(4, handler.Requests.Count);
         Assert.Empty(Directory.GetFiles(directory));
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>A client whose fake GitHub serves the given checksum file for any SHA256SUMS URL and the four known installer bytes for everything else.</summary>
   private static GitHubMsiReleaseClient DownloadClient(string checksums)
      => new(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal) ? Bytes(Encoding.UTF8.GetBytes(checksums)) : Bytes(Content)));



   /// <summary>A correct checksum line for the fixture installer bytes.</summary>
   private static string Checksums() => Convert.ToHexString(SHA256.HashData(Content)) + "  " + Name(Version) + "\n";



   /// <summary>The installer file name a release of this version is expected to carry.</summary>
   private static string Name(string version) => "QqqPrintAgent-v" + version + "-x64.msi";



   /// <summary>The genuine download URL GitHub uses for an asset of this release in this repository.</summary>
   private static string Url(string version, string name) => "https://github.com/QRun-IO/qqq-print-agent/releases/download/v" + version + "/" + name;



   /// <summary>A release record as the client would build it from valid metadata, so tests can alter one field at a time.</summary>
   private static MsiRelease ValidRelease(string version = Version) => new(version, new Uri(Url(version, Name(version))), new Uri(Url(version, "SHA256SUMS")), Content.Length);



   /// <summary>Creates a fresh empty staging folder; callers delete it when done.</summary>
   private static string TemporaryDirectory() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "qqq-update-test-" + Guid.NewGuid().ToString("N"))).FullName;



   /// <summary>One release asset in GitHub's API shape.</summary>
   private static object Asset(string version, string name, long size) => new { name, size, browser_download_url = Url(version, name) };



   /// <summary>A GitHub release entry carrying the installer and checksum assets, flagged prerelease exactly when the version is a release candidate.</summary>
   private static object Release(string version, bool draft = false) => new { tag_name = "v" + version, draft, prerelease = version.Contains("-rc.", StringComparison.Ordinal), assets = new[] { Asset(version, Name(version), Content.Length), Asset(version, "SHA256SUMS", 100) } };



   /// <summary>A 200 response whose body is the value serialized as JSON.</summary>
   private static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));



   /// <summary>A 200 response carrying the raw bytes.</summary>
   private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };



   /// <summary>A 302 response pointing at the given location.</summary>
   private static HttpResponseMessage Redirect(string target)
   {
      HttpResponseMessage response = new(HttpStatusCode.Found);
      response.Headers.Location = new Uri(target);
      return response;
   }



   /// <summary>Synchronous progress reporter, so the callback runs at the exact moment the download reports and the test can react deterministically.</summary>
   private sealed class InlineProgress(Action<double> report) : IProgress<double>
   {
      /// <summary>Forwards the value to the supplied callback immediately.</summary>
      public void Report(double value) => report(value);
   }



   /// <summary>Fake GitHub: records each requested URL and answers from the supplied function, honoring cancellation like a real handler.</summary>
   private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
   {
      internal List<string> Requests { get; } = [];



      /// <summary>Logs the URL, then returns the scripted response; throws if already cancelled.</summary>
      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         cancellationToken.ThrowIfCancellationRequested();
         Requests.Add(request.RequestUri!.AbsoluteUri);
         return Task.FromResult(response(request));
      }
   }
}
