/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using KofTwentyTwo.AppKit;


namespace Qqq.PrintAgent.Core;

/// <summary>Optional machine-deployed presentation settings, read once when the companion starts.</summary>
public sealed record RuntimeBranding(string? DisplayName = null, string? CompanyName = null, Uri? SupportUrl = null, string? AccentColor = null, string? LogoPath = null, string? IconPath = null)
{
   /// <summary>The machine-wide branding file under ProgramData, the one place customer branding is read from, so a user cannot override a deployment's labels.</summary>
   public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QqqPrintAgent", "branding.json");



   /// <summary>The MSI persists labels so unattended upgrades keep the customer identity.</summary>
   public static RuntimeBranding LoadMachine()
   {
      RuntimeBranding branding = Load(DefaultPath);
      if(!OperatingSystem.IsWindows())
      {
         return branding;
      }
      try
      {
         using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\QRunIO\PrintAgent");
         return branding with
         {
            DisplayName = branding.DisplayName ?? ValidMachineLabel(key?.GetValue("DisplayName") as string),
            CompanyName = branding.CompanyName ?? ValidMachineLabel(key?.GetValue("CompanyName") as string),
         };
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
      {
         return branding;
      }
   }



   /// <summary>Accepts a label stored by the installer only when it is non-blank, at most 180 characters and free of control characters; otherwise the built-in label is used.</summary>
   private static string? ValidMachineLabel(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 180 || value.Any(char.IsControl) ? null : value.Trim();



   /// <summary>Missing, unreadable, oversized or malformed settings fall back to the built-in appearance.</summary>
   public static RuntimeBranding Load(string path)
   {
      try
      {
         if(OperatingSystem.IsWindows())
         {
            MachineStateDirectory.ValidateExistingForPath(path);
         }
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
         string? name = ReadText(root, "displayName", 180);
         string? company = ReadText(root, "companyName", 180);
         string? support = ReadText(root, "supportUrl", 2048);
         Uri? supportUri = Uri.TryCreate(support, UriKind.Absolute, out Uri? candidate)
            && string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && string.IsNullOrEmpty(candidate.UserInfo) ? candidate : null;
         string? color = ReadText(root, "accentColor", 7);
         if(!HasReadableWhiteText(color))
         {
            color = null;
         }
         string? logo = ReadText(root, "logoPath", 260);
         string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
         return new(name, company, supportUri, color, ValidateLogo(logo, directory), ValidateIcon(ReadText(root, "iconPath", 260), directory));
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
      Tagline = CompanyName ?? (DisplayName is null ? defaults.Tagline : "Cloud printing"),
      Description = DisplayName is null && CompanyName is null ? defaults.Description : "Connect local printers to your print server.",
      Website = SupportUrl ?? defaults.Website,
      Brand = defaults.Brand with
      {
         GradientStart = HasReadableWhiteText(AccentColor) ? AccentColor! : defaults.Brand.GradientStart,
         GradientEnd = HasReadableWhiteText(AccentColor) ? AccentColor! : defaults.Brand.GradientEnd,
         IconIcoPath = IconPath ?? defaults.Brand.IconIcoPath,
      },
   };



   /// <summary>
   /// Accepts a branding icon only when it is a local .ico file (never a network share) of 22 bytes to 2 MiB whose header and
   /// image directory are self-consistent: the right signature, between 1 and 64 images, and every image inside the file. Anything else is ignored.
   /// </summary>
   private static string? ValidateIcon(string? value, string directory)
   {
      if(value is null)
      {
         return null;
      }
      string path = Path.GetFullPath(value, directory);
      if(path.StartsWith("\\\\", StringComparison.Ordinal) || !string.Equals(Path.GetExtension(path), ".ico", StringComparison.OrdinalIgnoreCase))
      {
         return null;
      }
      try
      {
         using FileStream stream = File.OpenRead(path);
         if(stream.Length is < 22 or > 2 * 1024 * 1024)
         {
            return null;
         }
         using BinaryReader reader = new(stream);
         if(reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
         {
            return null;
         }
         int count = reader.ReadUInt16();
         if(count is < 1 or > 64 || stream.Length < 6 + count * 16)
         {
            return null;
         }
         for(int index = 0; index < count; index++)
         {
            stream.Position = 6 + index * 16 + 8;
            uint length = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            if(length == 0 || offset < 6 + count * 16 || (long)offset + length > stream.Length)
            {
               return null;
            }
         }
         return path;
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         return null;
      }
   }



   /// <summary>True for a #RRGGBB accent colour dark enough that white text on it has a contrast ratio of at least 4.5 to 1, so a customer colour can never make the header unreadable.</summary>
   private static bool HasReadableWhiteText(string? color)
   {
      if(color is not { Length: 7 } || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
      {
         return false;
      }
      double luminance = 0.2126 * LinearChannel(color, 1) + 0.7152 * LinearChannel(color, 3) + 0.0722 * LinearChannel(color, 5);
      return 1.05 / (luminance + 0.05) >= 4.5;
   }



   /// <summary>Converts one two-digit hex colour channel to its linear light value, as the contrast formula requires.</summary>
   private static double LinearChannel(string color, int offset)
   {
      double value = int.Parse(color.AsSpan(offset, 2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture) / 255d;
      return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
   }



   /// <summary>Reads an optional text setting, trimmed, returning null when it is absent, not a string, empty, over the maximum length or contains control characters.</summary>
   private static string? ReadText(JsonElement root, string key, int maximum)
   {
      if(!root.TryGetProperty(key, out JsonElement field) || field.ValueKind != JsonValueKind.String)
      {
         return null;
      }
      string? value = field.GetString()?.Trim();
      return string.IsNullOrEmpty(value) || value.Length > maximum || value.Any(char.IsControl) ? null : value;
   }



   /// <summary>
   /// Accepts a branding logo only when it is a local .png file of at most 2 MiB whose own header says it is a real PNG no larger
   /// than 2048 by 2048 pixels. Network paths and other file types are refused, which also bounds how much image data the window will decode.
   /// </summary>
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
