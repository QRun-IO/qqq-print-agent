/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using KofTwentyTwo.AppKit;


namespace Qqq.PrintAgent.Core;

/// <summary>Optional machine-deployed presentation settings, read once when the companion starts.</summary>
public sealed record RuntimeBranding(string? DisplayName = null, string? CompanyName = null, Uri? SupportUrl = null, string? AccentColor = null, string? LogoPath = null)
{
   public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QqqPrintAgent", "branding.json");

   /// <summary>Missing, unreadable, oversized or malformed settings fall back to the built-in appearance.</summary>
   public static RuntimeBranding Load(string path)
   {
      try
      {
         using FileStream stream = File.OpenRead(path);
         if(stream.Length > 16 * 1024)
         {
            return new();
         }
         using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 4 });
         JsonElement root = document.RootElement;
         if(root.ValueKind != JsonValueKind.Object)
         {
            return new();
         }
         string? name = ReadText(root, "displayName", 60);
         string? company = ReadText(root, "companyName", 100);
         string? support = ReadText(root, "supportUrl", 2048);
         Uri? supportUri = Uri.TryCreate(support, UriKind.Absolute, out Uri? candidate)
            && string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && string.IsNullOrEmpty(candidate.UserInfo) ? candidate : null;
         string? color = ReadText(root, "accentColor", 7);
         if(color is not { Length: 7 } || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
         {
            color = null;
         }
         string? logo = ReadText(root, "logoPath", 260);
         return new(name, company, supportUri, color, ValidateLogo(logo, Path.GetDirectoryName(Path.GetFullPath(path))!));
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
      {
         return new();
      }
   }

   /// <summary>Apply presentation without changing application identity, source attribution or storage paths.</summary>
   public AppInfo Apply(AppInfo defaults) => defaults with
   {
      DisplayName = DisplayName ?? defaults.DisplayName,
      Tagline = CompanyName ?? defaults.Tagline,
      Description = CompanyName is null ? defaults.Description : $"Print connector provided by {CompanyName}. Connect local printers to QQQ.",
      Website = SupportUrl ?? defaults.Website,
      Brand = AccentColor is null ? defaults.Brand : defaults.Brand with { GradientStart = AccentColor, GradientEnd = AccentColor },
   };

   private static string? ReadText(JsonElement root, string key, int maximum)
   {
      if(!root.TryGetProperty(key, out JsonElement field) || field.ValueKind != JsonValueKind.String)
      {
         return null;
      }
      string? value = field.GetString()?.Trim();
      return string.IsNullOrEmpty(value) || value.Length > maximum || value.Any(char.IsControl) ? null : value;
   }

   private static string? ValidateLogo(string? value, string directory)
   {
      if(value is null || value.Contains(':') && !Path.IsPathFullyQualified(value))
      {
         return null;
      }
      string path = Path.GetFullPath(value, directory);
      var uri = new Uri(path);
      if(!uri.IsFile || uri.IsUnc || path.StartsWith("\\\\", StringComparison.Ordinal)
         || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
      {
         return null;
      }
      try
      {
         using FileStream stream = File.OpenRead(path);
         if(stream.Length > 2 * 1024 * 1024)
         {
            return null;
         }
         Span<byte> signature = stackalloc byte[24];
         stream.ReadExactly(signature);
         ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
         int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(signature[16..20]);
         int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(signature[20..24]);
         return signature[..8].SequenceEqual(png) && width is > 0 and <= 2048 && height is > 0 and <= 2048 ? path : null;
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         return null;
      }
   }
}
