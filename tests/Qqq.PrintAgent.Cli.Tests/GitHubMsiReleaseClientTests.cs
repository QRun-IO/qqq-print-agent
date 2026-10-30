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

public sealed class GitHubMsiReleaseClientTests
{
   private const string Version = "0.1.3";
   private static readonly byte[] Content = [1, 2, 3, 4];

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

   [Fact]
   public async Task StableReleaseSupersedesSameVersionCandidate()
   {
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { Release("0.1.3-rc.10"), Release("0.1.3") })));
      Assert.Equal("0.1.3", (await client.FindUpdateAsync("0.1.3-rc.9"))?.Version);
   }

   [Theory]
   [InlineData("0.1.3")]
   [InlineData("1.0.0")]
   public async Task DoesNotOfferCurrentOrOlderRelease(string installed)
   {
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { Release("0.1.3"), Release("0.1.2"), Release("0.1.1") })));
      Assert.Null(await client.FindUpdateAsync(installed));
   }

   [Fact]
   public async Task DoesNotOfferKnownBrokenLegacyVersions()
   {
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { Release("0.1.1"), Release("0.1.2-rc.1"), Release("0.1.2") })));
      Assert.Null(await client.FindUpdateAsync("0.1.0"));
   }

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

   [Fact]
   public async Task RejectsVersionAndPrereleaseFlagMismatch()
   {
      object invalid = new { draft = false, tag_name = "v0.1.3", prerelease = true, assets = Array.Empty<object>() };
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { invalid })));
      await Assert.ThrowsAsync<InvalidDataException>(() => client.FindUpdateAsync("0.1.2-rc.1"));
   }

   [Fact]
   public async Task RejectsDuplicateExpectedReleaseAssets()
   {
      object invalid = new { draft = false, tag_name = "v0.1.3", prerelease = false, assets = new[] { Asset(Version, Name(Version), 4), Asset(Version, Name(Version), 4), Asset(Version, "SHA256SUMS", 100) } };
      using GitHubMsiReleaseClient client = new(new Handler(_ => Json(new[] { invalid })));
      await Assert.ThrowsAsync<InvalidDataException>(() => client.FindUpdateAsync("0.1.2"));
   }

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

   [Fact]
   public async Task RejectsOversizeMetadataAndDeclaredMsi()
   {
      using GitHubMsiReleaseClient metadata = new(new Handler(_ => Bytes(new byte[2 * 1024 * 1024 + 1])));
      await Assert.ThrowsAsync<InvalidDataException>(() => metadata.FindUpdateAsync("0.1.2"));
      using GitHubMsiReleaseClient download = DownloadClient(Checksums());
      await Assert.ThrowsAsync<InvalidDataException>(() => download.DownloadVerifiedAsync(ValidRelease() with { Size = 256L * 1024 * 1024 + 1 }, "0.1.2", Path.GetTempPath()));
   }

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

   private static GitHubMsiReleaseClient DownloadClient(string checksums)
      => new(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("/SHA256SUMS", StringComparison.Ordinal) ? Bytes(Encoding.UTF8.GetBytes(checksums)) : Bytes(Content)));

   private static string Checksums() => Convert.ToHexString(SHA256.HashData(Content)) + "  " + Name(Version) + "\n";
   private static string Name(string version) => "QqqPrintAgent-v" + version + "-x64.msi";
   private static string Url(string version, string name) => "https://github.com/QRun-IO/qqq-print-agent/releases/download/v" + version + "/" + name;
   private static MsiRelease ValidRelease(string version = Version) => new(version, new Uri(Url(version, Name(version))), new Uri(Url(version, "SHA256SUMS")), Content.Length);
   private static string TemporaryDirectory() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "qqq-update-test-" + Guid.NewGuid().ToString("N"))).FullName;
   private static object Asset(string version, string name, long size) => new { name, size, browser_download_url = Url(version, name) };
   private static object Release(string version, bool draft = false) => new { tag_name = "v" + version, draft, prerelease = version.Contains("-rc.", StringComparison.Ordinal), assets = new[] { Asset(version, Name(version), Content.Length), Asset(version, "SHA256SUMS", 100) } };
   private static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));
   private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };

   private static HttpResponseMessage Redirect(string target)
   {
      HttpResponseMessage response = new(HttpStatusCode.Found);
      response.Headers.Location = new Uri(target);
      return response;
   }

   private sealed class InlineProgress(Action<double> report) : IProgress<double>
   {
      public void Report(double value) => report(value);
   }

   private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
   {
      internal List<string> Requests { get; } = [];

      protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         cancellationToken.ThrowIfCancellationRequested();
         Requests.Add(request.RequestUri!.AbsoluteUri);
         return Task.FromResult(response(request));
      }
   }
}
