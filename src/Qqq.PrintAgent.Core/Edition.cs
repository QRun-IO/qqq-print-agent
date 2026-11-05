/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using System.Text.RegularExpressions;
using KofTwentyTwo.AppKit;


namespace Qqq.PrintAgent.Core;

/// <summary>
/// A client edition compiled into a build. The build script copies one folder under <c>editions/</c> into the companion
/// (its mark, icon and this descriptor as <c>edition.json</c> beside the executable), stamps the client's product and company
/// into every executable, and names the installer and release asset after <see cref="AssetPrefix"/>, so the installed
/// updater follows the client's own asset. An edition changes presentation and labels only: application id, data paths,
/// executable names, the install directory, the protocol handler and the MSI upgrade identity stay shared. Machine
/// branding (<see cref="RuntimeBranding"/>) still applies on top of an edition.
/// </summary>
public sealed partial record Edition(string Id, string DisplayName, string CompanyName, string? Tagline, string? Description, Uri? SupportUrl, string? AccentColor, string AssetPrefix,
   string ServerName = Edition.DefaultServerName, bool ShowMascot = true, bool ShowLicense = true)
{
   /// <summary>What the generic build calls the server in user-facing text.</summary>
   public const string DefaultServerName = "QQQ";

   /// <summary>The descriptor's file name, next to the executable in an edition build.</summary>
   public const string FileName = "edition.json";

   /// <summary>The release asset prefix of the generic build, used when no edition is compiled in.</summary>
   public const string DefaultAssetPrefix = "QqqPrintAgent";



   /// <summary>Reads the edition compiled into this build, or null for the generic build.</summary>
   public static Edition? LoadInstalled() => Load(Path.Combine(AppContext.BaseDirectory, FileName));



   /// <summary>
   /// Reads an edition descriptor. A missing file means the generic build; a malformed one (bad id or asset prefix, missing
   /// names, oversized, not an object) is treated the same way rather than half-applied, and an accent colour is kept only
   /// when white text stays readable on it.
   /// </summary>
   public static Edition? Load(string path)
   {
      try
      {
         if(!File.Exists(path))
         {
            return null;
         }
         using FileStream stream = File.OpenRead(path);
         if(stream.Length > 16 * 1024)
         {
            return null;
         }
         using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
         JsonElement root = document.RootElement;
         if(root.ValueKind != JsonValueKind.Object)
         {
            return null;
         }
         string? id = RuntimeBranding.ReadText(root, "id", 40);
         string? name = RuntimeBranding.ReadText(root, "displayName", 180);
         string? company = RuntimeBranding.ReadText(root, "companyName", 180);
         string? prefix = RuntimeBranding.ReadText(root, "assetPrefix", 40);
         if(id is null || !IdPattern().IsMatch(id) || name is null || company is null || prefix is null || !AssetPrefixPattern().IsMatch(prefix))
         {
            return null;
         }
         string? support = RuntimeBranding.ReadText(root, "supportUrl", 2048);
         Uri? supportUri = Uri.TryCreate(support, UriKind.Absolute, out Uri? candidate)
            && string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && string.IsNullOrEmpty(candidate.UserInfo) ? candidate : null;
         string? accent = RuntimeBranding.ReadText(root, "accentColor", 7);
         return new(id, name, company, RuntimeBranding.ReadText(root, "tagline", 180), RuntimeBranding.ReadText(root, "description", 180), supportUri,
            RuntimeBranding.HasReadableWhiteText(accent) ? accent : null, prefix,
            RuntimeBranding.ReadText(root, "serverName", 60) ?? DefaultServerName, ReadFlag(root, "aboutMascot"), ReadFlag(root, "aboutLicense"));
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException)
      {
         return null;
      }
   }



   /// <summary>Applies the edition's presentation to the generic identity without touching id, author, copyright, repository or attributions.</summary>
   public AppInfo Apply(AppInfo defaults)
   {
      ArgumentNullException.ThrowIfNull(defaults);
      return defaults with
      {
         DisplayName = DisplayName,
         Tagline = Tagline ?? CompanyName,
         Description = Description ?? defaults.Description,
         Website = SupportUrl ?? defaults.Website,
         Brand = defaults.Brand with
         {
            GradientStart = AccentColor ?? defaults.Brand.GradientStart,
            GradientEnd = AccentColor ?? defaults.Brand.GradientEnd,
         },
      };
   }



   /// <summary>Reads an optional boolean switch; anything but an explicit false keeps the generic behaviour.</summary>
   private static bool ReadFlag(JsonElement root, string key)
      => !(root.TryGetProperty(key, out JsonElement field) && field.ValueKind == JsonValueKind.False);



   /// <summary>An edition id is a folder name: lowercase letters, digits and hyphens, at most 40 characters.</summary>
   [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant, 1000)]
   private static partial Regex IdPattern();



   /// <summary>An asset prefix becomes a file name on the release feed: 2 to 40 letters or digits starting with a letter.</summary>
   [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{1,39}$", RegexOptions.CultureInvariant, 1000)]
   private static partial Regex AssetPrefixPattern();
}
