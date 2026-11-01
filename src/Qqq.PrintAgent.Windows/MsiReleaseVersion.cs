/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Runtime.InteropServices;


namespace Qqq.PrintAgent.Windows;

/// <summary>
/// A release version of the form X.Y.Z or X.Y.Z-rc.N, ordered the way semantic versioning orders them: a release candidate
/// sorts before the final release with the same numbers. The updater uses this to offer only strictly newer releases.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct MsiReleaseVersion(int Major, int Minor, int Patch, int? Candidate) : IComparable<MsiReleaseVersion>
{
   /// <summary>True when this version sorts before the other, so versions can be compared with the usual operators.</summary>
   public static bool operator <(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) < 0;



   /// <summary>True when this version sorts after the other.</summary>
   public static bool operator >(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) > 0;



   /// <summary>True when this version sorts before or equal to the other.</summary>
   public static bool operator <=(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) <= 0;



   /// <summary>True when this version sorts after or equal to the other.</summary>
   public static bool operator >=(MsiReleaseVersion left, MsiReleaseVersion right) => left.CompareTo(right) >= 0;



   /// <summary>Orders by major, minor and patch, then puts a release candidate before the final release and orders candidates by their number.</summary>
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



   /// <summary>True for 0.1.3 and later, the first version whose updater and installer layout can be updated in place; older installs are not offered updates.</summary>
   internal bool MeetsMinimum => Major > 0 || Minor > 1 || Minor == 1 && Patch >= 3;



   /// <summary>
   /// Parses the running build's informational version, tolerating a leading "v" and build metadata after "+" (a source
   /// revision) when that metadata is made only of dot-separated letters, digits and hyphens. Anything else throws, so a
   /// malformed installed version is reported rather than guessed.
   /// </summary>
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



   /// <summary>Parses X.Y.Z or X.Y.Z-rc.N with canonical numbers only (no signs, spaces or leading zeros), returning false instead of throwing for any other text.</summary>
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



   /// <summary>Accepts a non-negative integer only when printing it back gives the same text, which rejects leading zeros and any variant spelling that two parsers could read differently.</summary>
   private static bool Number(string text, out int value)
      => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && string.Equals(text, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
