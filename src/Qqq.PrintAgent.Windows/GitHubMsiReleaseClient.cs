/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;


namespace Qqq.PrintAgent.Windows;

/// <summary>Discovers only this application's releases and stages an integrity-checked machine MSI.</summary>
public sealed class GitHubMsiReleaseClient : IDisposable
{
   private const long MaximumMsiBytes = 256L * 1024 * 1024;
   private const int MaximumMetadataBytes = 2 * 1024 * 1024;
   private const int MaximumChecksumBytes = 64 * 1024;
   private const string RepositoryPath = "/QRun-IO/qqq-print-agent/releases/download/";
   private static readonly Uri Feed = new("https://api.github.com/repos/QRun-IO/qqq-print-agent/releases?per_page=100");
   private readonly HttpClient _http;



   /// <summary>
   /// Creates the client with automatic redirects off, because every redirect is validated by hand against an allow-list of
   /// GitHub hosts. A caller-supplied handler lets tests substitute the network without relaxing that rule.
   /// </summary>
   public GitHubMsiReleaseClient(HttpMessageHandler? handler = null)
   {
      _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
      {
         Timeout = TimeSpan.FromMinutes(10),
      };
      _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("QQQ-Print-Agent", "1"));
   }



   /// <summary>
   /// Selects a strictly newer supported release from this repository's release feed, excluding drafts and unsafe legacy
   /// installers. The whole check has a two minute limit, the feed is size and shape bounded, a release whose prerelease flag
   /// disagrees with its tag is rejected, and each candidate must carry exactly one MSI and one checksum file of allowed size.
   /// </summary>
   public async Task<MsiRelease?> FindUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
   {
      using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      deadline.CancelAfter(TimeSpan.FromMinutes(2));
      cancellationToken = deadline.Token;
      var installed = MsiReleaseVersion.ParseCurrent(currentVersion);
      using HttpResponseMessage response = await GetAsync(Feed, metadata: true, cancellationToken).ConfigureAwait(false);
      byte[] bytes = await ReadBoundedAsync(response, MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
      using var document = JsonDocument.Parse(bytes);
      if(document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 100)
      {
         throw new InvalidDataException("The release feed has an unexpected shape.");
      }
      MsiRelease? selected = null;
      MsiReleaseVersion selectedVersion = installed;
      foreach(JsonElement release in document.RootElement.EnumerateArray())
      {
         if(release.GetProperty("draft").GetBoolean())
         {
            continue;
         }
         string tag = release.GetProperty("tag_name").GetString() ?? "";
         if(!tag.StartsWith('v') || !MsiReleaseVersion.TryParse(tag[1..], out MsiReleaseVersion version) || !Eligible(version, installed))
         {
            continue;
         }
         if(release.GetProperty("prerelease").GetBoolean() != (version.Candidate is not null))
         {
            throw new InvalidDataException("Release tag and prerelease metadata disagree.");
         }
         string textVersion = tag[1..];
         JsonElement assets = release.GetProperty("assets");
         string name = AssetName(textVersion);
         JsonElement msi = SingleAsset(assets, name);
         JsonElement sums = SingleAsset(assets, "SHA256SUMS");
         long size = msi.GetProperty("size").GetInt64();
         long sumsSize = sums.GetProperty("size").GetInt64();
         if(size is <= 0 or > MaximumMsiBytes || sumsSize is <= 0 or > MaximumChecksumBytes)
         {
            throw new InvalidDataException("Release assets exceed their allowed sizes.");
         }
         MsiRelease candidate = new(textVersion, AssetUri(msi), AssetUri(sums), size);
         Validate(candidate, installed);
         if(version.CompareTo(selectedVersion) > 0)
         {
            selected = candidate;
            selectedVersion = version;
         }
      }
      return selected;
   }



   /// <summary>
   /// Downloads the release's MSI into a caller-supplied, access-restricted staging directory and returns only the verified,
   /// uniquely named path. The expected hash comes from the release's checksum file, the file is written to a partial name
   /// while its size and SHA-256 are checked as it streams, and it is moved into place only when both match, so a corrupt or
   /// truncated download can never be mistaken for an installer. A failed download removes its own partial file.
   /// </summary>
   public async Task<string> DownloadVerifiedAsync(MsiRelease release, string currentVersion, string stagingDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
   {
      using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      deadline.CancelAfter(TimeSpan.FromMinutes(10));
      cancellationToken = deadline.Token;
      Validate(release, MsiReleaseVersion.ParseCurrent(currentVersion));
      cancellationToken.ThrowIfCancellationRequested();
      using HttpResponseMessage sumsResponse = await GetAsync(release.ChecksumUrl, metadata: false, cancellationToken).ConfigureAwait(false);
      byte[] sums = await ReadBoundedAsync(sumsResponse, MaximumChecksumBytes, cancellationToken).ConfigureAwait(false);
      byte[] expected = ReadChecksum(new UTF8Encoding(false, true).GetString(sums), AssetName(release.Version));
      string directory = Path.GetFullPath(stagingDirectory);
      Directory.CreateDirectory(directory);
      if(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
      {
         throw new InvalidDataException("The update staging directory cannot be a reparse point.");
      }
      string destination = Path.Combine(directory, "QqqPrintAgent-v" + release.Version + "-x64-" + Guid.NewGuid().ToString("N") + ".msi");
      string partial = destination + ".partial";
      bool owned = false;
      try
      {
         using HttpResponseMessage response = await GetAsync(release.MsiUrl, metadata: false, cancellationToken).ConfigureAwait(false);
         if(response.Content.Headers.ContentLength is long length && length != release.Size)
         {
            throw new InvalidDataException("The MSI size does not match release metadata.");
         }
         await using(Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
         await using(FileStream target = new(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
         {
            owned = true;
            await CopyVerifiedAsync(source, target, release.Size, expected, progress, cancellationToken).ConfigureAwait(false);
         }
         cancellationToken.ThrowIfCancellationRequested();
         File.Move(partial, destination);
         owned = false;
         return destination;
      }
      finally
      {
         if(owned && File.Exists(partial))
         {
            File.Delete(partial);
         }
      }
   }



   /// <summary>Releases the HTTP client.</summary>
   public void Dispose() => _http.Dispose();



   /// <summary>
   /// Copies the stream to disk while hashing it and enforcing the declared size, aborting the moment the data exceeds the
   /// declared size or the absolute cap. The hash is compared in constant time at the end, and reported progress is the
   /// fraction of the declared size received so far.
   /// </summary>
   private static async Task CopyVerifiedAsync(Stream source, Stream target, long size, byte[] expected, IProgress<double>? progress, CancellationToken token)
   {
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      byte[] buffer = new byte[81920];
      long total = 0;
      int read;
      while((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
      {
         total += read;
         if(total > size || total > MaximumMsiBytes)
         {
            throw new InvalidDataException("The MSI exceeded its declared size.");
         }
         hash.AppendData(buffer.AsSpan(0, read));
         await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
         progress?.Report((double)total / size);
      }
      if(total != size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
      {
         throw new InvalidDataException("MSI integrity verification failed. No update was staged.");
      }
      await target.FlushAsync(token).ConfigureAwait(false);
   }



   /// <summary>
   /// A candidate is eligible when it meets the minimum supported version and is strictly newer than the installed one.
   /// A stable install ignores prereleases; a prerelease install may take a newer prerelease or stable release.
   /// </summary>
   private static bool Eligible(MsiReleaseVersion candidate, MsiReleaseVersion current)
      => candidate.MeetsMinimum && candidate.CompareTo(current) > 0 && (current.Candidate is not null || candidate.Candidate is null);



   /// <summary>Rechecks a release description before it is used: version and channel eligibility, size, and that both URLs are exactly this repository's asset addresses.</summary>
   private static void Validate(MsiRelease release, MsiReleaseVersion current)
   {
      if(!MsiReleaseVersion.TryParse(release.Version, out MsiReleaseVersion candidate) || !Eligible(candidate, current) || release.Size is <= 0 or > MaximumMsiBytes)
      {
         throw new InvalidDataException("The update version, channel or size is not eligible.");
      }
      ValidateAssetUri(release.MsiUrl, release.Version, AssetName(release.Version));
      ValidateAssetUri(release.ChecksumUrl, release.Version, "SHA256SUMS");
   }



   /// <summary>The single MSI file name a release must publish for a version; the updater depends on this exact name.</summary>
   private static string AssetName(string version) => "QqqPrintAgent-v" + version + "-x64.msi";



   /// <summary>Finds the one asset with the given name; zero or several matches is an error, so an ambiguous release can never choose a file by accident.</summary>
   private static JsonElement SingleAsset(JsonElement assets, string name)
   {
      if(assets.ValueKind != JsonValueKind.Array)
      {
         throw new InvalidDataException("Release assets are not an array.");
      }
      JsonElement[] matches = assets.EnumerateArray().Where(asset => string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.Ordinal)).ToArray();
      return matches.Length == 1 ? matches[0] : throw new InvalidDataException("The release must contain exactly one " + name + " asset.");
   }



   /// <summary>Reads an asset's download address as an absolute URL; the address is checked against the repository path separately.</summary>
   private static Uri AssetUri(JsonElement asset)
      => Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out Uri? uri) ? uri : throw new InvalidDataException("Invalid release asset URL.");



   /// <summary>Requires the URL to equal, character for character, this repository's release path for the version and file name, so a feed entry cannot point elsewhere.</summary>
   private static void ValidateAssetUri(Uri uri, string version, string asset)
   {
      string expected = "https://github.com" + RepositoryPath + "v" + version + "/" + asset;
      if(!uri.IsAbsoluteUri || !string.Equals(uri.AbsoluteUri, expected, StringComparison.Ordinal) || !string.Equals(uri.OriginalString, expected, StringComparison.Ordinal))
      {
         throw new InvalidDataException("The release asset URL does not match its repository, version and filename.");
      }
   }



   /// <summary>
   /// Parses a SHA256SUMS file strictly and returns the hash for the expected file. Each line must be a 64 digit hex hash
   /// and a plain file name with no path or control characters, duplicates are rejected, and the expected file must be present.
   /// </summary>
   private static byte[] ReadChecksum(string text, string expectedName)
   {
      HashSet<string> filenames = new(StringComparer.OrdinalIgnoreCase);
      byte[]? selected = null;
      foreach(string raw in text.TrimStart('\uFEFF').Split('\n'))
      {
         string line = raw.TrimEnd('\r');
         if(line.Length == 0)
         {
            continue;
         }
         if(line.Length < 67 || line[64] != ' ' || line[65] is not (' ' or '*') || !line[..64].All(char.IsAsciiHexDigit))
         {
            throw new InvalidDataException("Malformed SHA256SUMS entry.");
         }
         string name = line[66..];
         if(name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(char.IsControl) || !filenames.Add(name))
         {
            throw new InvalidDataException("Unsafe or duplicate SHA256SUMS filename.");
         }
         if(string.Equals(name, expectedName, StringComparison.Ordinal))
         {
            selected = Convert.FromHexString(line[..64]);
         }
      }
      return selected ?? throw new InvalidDataException("The expected MSI is missing from SHA256SUMS.");
   }



   /// <summary>
   /// Gets a URL, following at most five redirects by hand. Metadata never redirects. An asset may redirect only to the same
   /// path on github.com or to GitHub's release asset hosts, over HTTPS on the default port with no credentials, so a
   /// compromised redirect cannot move the download to another site.
   /// </summary>
   private async Task<HttpResponseMessage> GetAsync(Uri initial, bool metadata, CancellationToken token)
   {
      Uri current = initial;
      for(int redirects = 0; redirects <= 5; redirects++)
      {
         HttpResponseMessage response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
         if(response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
         {
            if(!response.IsSuccessStatusCode)
            {
               response.Dispose();
               throw new HttpRequestException("The update server did not return a successful response.");
            }
            return response;
         }
         Uri? location = response.Headers.Location;
         response.Dispose();
         if(metadata || location is null)
         {
            throw new InvalidDataException("Unexpected update metadata redirect.");
         }
         current = location.IsAbsoluteUri ? location : new Uri(current, location);
         bool github = string.Equals(current.Host, "github.com", StringComparison.OrdinalIgnoreCase) && string.Equals(current.AbsolutePath, initial.AbsolutePath, StringComparison.Ordinal);
         bool cdn = string.Equals(current.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || string.Equals(current.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
         if(!string.Equals(current.Scheme, "https", StringComparison.Ordinal) || !current.IsDefaultPort || current.UserInfo.Length != 0 || current.Fragment.Length != 0 || (!github && !cdn))
         {
            throw new InvalidDataException("Unsafe release asset redirect.");
         }
      }
      throw new InvalidDataException("Too many release asset redirects.");
   }



   /// <summary>Reads a small response into memory but never more than the stated limit, whether announced or discovered while reading.</summary>
   private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
   {
      if(response.Content.Headers.ContentLength > maximum)
      {
         throw new InvalidDataException("Update metadata exceeds its allowed size.");
      }
      await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
      using MemoryStream result = new();
      byte[] buffer = new byte[8192];
      int read;
      while((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
      {
         if(result.Length + read > maximum)
         {
            throw new InvalidDataException("Update metadata exceeds its allowed size.");
         }
         await result.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
      }
      return result.ToArray();
   }
}
