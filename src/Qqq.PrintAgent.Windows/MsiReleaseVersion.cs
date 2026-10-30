/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Runtime.InteropServices;


namespace Qqq.PrintAgent.Windows;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct MsiReleaseVersion(int Major, int Minor, int Patch, int? Candidate) : IComparable<MsiReleaseVersion>
{
   public static bool operator <(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) < 0;
   public static bool operator >(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) > 0;
   public static bool operator <=(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) <= 0;
   public static bool operator >=(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) >= 0;

   public int CompareTo(MsiReleaseVersion other)
   {
      int result = Major.CompareTo(other.Major);
      if(result == 0)
      {
         result = Minor.CompareTo(other.Minor);
      }
      if(result == 0)
      {
         result = Patch.CompareTo(other.Patch);
      }
      if(result != 0)
      {
         return result;
      }
      return Candidate is null ? other.Candidate is null ? 0 : 1 : other.Candidate is null ? -1 : Candidate.Value.CompareTo(other.Candidate.Value);
   }

   internal bool MeetsMinimum => Major > 0 || Minor > 1 || Minor == 1 && Patch >= 3;

   internal static MsiReleaseVersion ParseCurrent(string text)
   {
      string value = text.StartsWith('v') ? text[1..] : text;
      string[] metadata = value.Split('+');
      if(metadata.Length > 2 || metadata.Length == 2 && (metadata[1].Length == 0 || metadata[1].Split('.').Any(part => part.Length == 0 || part.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))))
      {
         throw new ArgumentException("The installed version is not a supported semantic version.", nameof(text));
      }
      if(!TryParse(metadata[0], out MsiReleaseVersion version))
      {
         throw new ArgumentException("The installed version must be X.Y.Z or X.Y.Z-rc.N.", nameof(text));
      }
      return version;
   }

   internal static bool TryParse(string text, out MsiReleaseVersion version)
   {
      version = default;
      string[] parts = text.Split("-rc.", StringSplitOptions.None);
      if(parts.Length is < 1 or > 2)
      {
         return false;
      }
      string[] core = parts[0].Split('.');
      if(core.Length != 3 || !Number(core[0], out int major) || !Number(core[1], out int minor) || !Number(core[2], out int patch))
      {
         return false;
      }
      int? candidate = null;
      if(parts.Length == 2)
      {
         if(!Number(parts[1], out int parsed))
         {
            return false;
         }
         candidate = parsed;
      }
      version = new MsiReleaseVersion(major, minor, patch, candidate);
      return true;
   }

   private static bool Number(string text, out int value)
      => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && string.Equals(text, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
