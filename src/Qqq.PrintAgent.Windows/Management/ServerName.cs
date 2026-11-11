/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What a server's name may be (ADR-0004, "Server names"). The rule is enforced where a name enters the service, in the
/// arguments of <see cref="ManagementOperations.ServersAdd"/> and <see cref="ManagementOperations.ServersRename"/>, and
/// a name that breaks it is refused there with <see cref="RefusalCode"/>. A name is accepted when, after surrounding
/// white space is removed:
/// <list type="bullet">
/// <item>it has 1 to <see cref="MaximumCharacters"/> UTF-16 units;</item>
/// <item>every code point of it is one Core's definition of printable text shows (<see cref="ServerText.IsShown"/>), and no surrogate stands without its partner;</item>
/// <item>it holds no square bracket, which would forge structure in a line that puts something in brackets;</item>
/// <item>it is not shaped like a server identifier or like the first eight or more characters of one, in either letter case, because that is what the command line's <c>--server</c> takes as an identifier or a prefix (<see cref="ServerSelector"/>).</item>
/// </list>
/// The companion's wizard and the upgrade propose a name, and use this to propose only one that passes.
/// </summary>
public static class ServerName
{
   /// <summary>The most UTF-16 units a name may have, after surrounding white space is removed.</summary>
   public const int MaximumCharacters = 64;



   /// <summary>The code a request is refused with when it gives a name that breaks the rule.</summary>
   public const string RefusalCode = ManagementErrorCode.InvalidRequest;



   /// <summary>How many characters a server identifier has in the one form the wire carries: 32 hexadecimal digits and four hyphens.</summary>
   private const int IdentifierLength = 36;



   /// <summary>Decides whether a name may be a server's name, and returns it as it is stored: with surrounding white space removed and nothing else changed.</summary>
   public static bool TryAccept(string? name, [NotNullWhen(true)] out string? accepted)
   {
      string trimmed = name?.Trim() ?? "";
      accepted = trimmed.Length is >= 1 and <= MaximumCharacters && IsShownWithoutBrackets(trimmed) && !IsIdentifierOrItsBeginning(trimmed) ? trimmed : null;
      return accepted is not null;
   }



   /// <summary>Returns the name as it is stored, or refuses one that breaks the rule. The refusal is fixed text and never repeats the name.</summary>
   public static string Require(string? name)
      => TryAccept(name, out string? accepted) ? accepted : throw new ArgumentException("A server's name is 1 to 64 characters of visible text without square brackets, and is not shaped like a server identifier.", nameof(name));



   /// <summary>True when the text is made only of whole code points that are shown as themselves, none of them a square bracket.</summary>
   private static bool IsShownWithoutBrackets(string name)
   {
      for(int index = 0; index < name.Length;)
      {
         if(Rune.DecodeFromUtf16(name.AsSpan(index), out Rune rune, out int consumed) != OperationStatus.Done || !ServerText.IsShown(rune) || rune.Value is '[' or ']')
         {
            return false;
         }
         index += consumed;
      }
      return true;
   }



   /// <summary>
   /// True when the text could be a server identifier or the first eight or more characters of one: hexadecimal digits
   /// in either letter case with a hyphen in each place the identifier's one form has one. The command line compares a
   /// <c>--server</c> value with identifiers without regard to letter case, so a name of this shape could select
   /// another server than the one that bears it.
   /// </summary>
   private static bool IsIdentifierOrItsBeginning(string name)
   {
      if(name.Length is < ServerSelector.MinimumPrefixLength or > IdentifierLength)
      {
         return false;
      }
      for(int index = 0; index < name.Length; index++)
      {
         bool placeOfAHyphen = index is 8 or 13 or 18 or 23;
         if(placeOfAHyphen ? name[index] != '-' : !char.IsAsciiHexDigit(name[index]))
         {
            return false;
         }
      }
      return true;
   }
}
