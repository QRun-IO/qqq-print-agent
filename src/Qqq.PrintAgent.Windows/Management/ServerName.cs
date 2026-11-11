/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What a server's name may be (ADR-0004, "Server names"): the one definition the service and the companion both call.
/// The service enforces it where a name enters, in the arguments of <see cref="ManagementOperations.ServersAdd"/> and
/// <see cref="ManagementOperations.ServersRename"/>, and a name that breaks it is refused there with
/// <see cref="RefusalCode"/>; the companion asks <see cref="Check"/> while the operator types, so it can say what is
/// wrong before anything is sent. A name is accepted when, after surrounding white space is removed:
/// <list type="bullet">
/// <item>it has 1 to <see cref="MaximumCharacters"/> UTF-16 units;</item>
/// <item>every code point of it is one Core's definition of printable text shows (<see cref="ServerText.IsShown"/>), and no surrogate stands without its partner;</item>
/// <item>it holds no square bracket, because square brackets are the delimiters of the service log's line prefixes (the marker of the service, a server's short identifier) and of other lines that put something in brackets, so a bracket in a name could forge that structure wherever a name is written into such a line. The fullwidth pair (U+FF3B, U+FF3D) is refused with the ASCII pair: it is not a delimiter to a program, but it reads as the same brackets to a person;</item>
/// <item>it is not shaped like a server identifier or like the first eight or more characters of one, in either letter case, because that is what the command line's <c>--server</c> takes as an identifier or a prefix (<see cref="ServerSelector"/>).</item>
/// </list>
/// The upgrade and the wizard propose a name from the server's address, and a host can break the rule (the brackets of
/// an IPv6 literal, a host of eight hexadecimal digits); <see cref="Propose"/> is how they get one that always passes.
/// </summary>
public static class ServerName
{
   /// <summary>The most UTF-16 units a name may have, after surrounding white space is removed.</summary>
   public const int MaximumCharacters = 64;



   /// <summary>The code a request is refused with when it gives a name that breaks the rule.</summary>
   public const string RefusalCode = ManagementErrorCode.InvalidRequest;



   /// <summary>The square brackets a name may not hold: the ASCII pair, which the service log's prefixes are delimited by, and the fullwidth pair (U+FF3B, U+FF3D), which a person reads as the same brackets.</summary>
   private static readonly SearchValues<char> s_squareBrackets = SearchValues.Create("[]\uFF3B\uFF3D");



   /// <summary>How many characters a server identifier has in the one form the wire carries: 32 hexadecimal digits and four hyphens.</summary>
   private const int IdentifierLength = 36;



   /// <summary>What a proposed name is when the address gives nothing to name the server by, and what goes in front of a host that is shaped like an identifier.</summary>
   private const string Fallback = "Server";



   /// <summary>
   /// Says what is wrong with a name, or <see cref="ServerNameProblem.None"/>. Surrounding white space is not part of
   /// the name and is not judged. When a name breaks the rule in several ways the first of them in the order of
   /// <see cref="ServerNameProblem"/> is reported, so the same name always gets the same answer.
   /// </summary>
   public static ServerNameProblem Check(string? name)
   {
      string trimmed = name?.Trim() ?? "";
      return trimmed.Length == 0 ? ServerNameProblem.Empty
         : trimmed.Length > MaximumCharacters ? ServerNameProblem.TooLong
         : !IsPrintable(trimmed) ? ServerNameProblem.NotPrintable
         : trimmed.AsSpan().ContainsAny(s_squareBrackets) ? ServerNameProblem.SquareBracket
         : IsIdentifierOrItsBeginning(trimmed) ? ServerNameProblem.LikeAnIdentifier
         : ServerNameProblem.None;
   }



   /// <summary>Decides whether a name may be a server's name, and returns it as it is stored: with surrounding white space removed and nothing else changed.</summary>
   public static bool TryAccept(string? name, [NotNullWhen(true)] out string? accepted)
   {
      accepted = Check(name) == ServerNameProblem.None ? name!.Trim() : null;
      return accepted is not null;
   }



   /// <summary>Returns the name as it is stored, or refuses one that breaks the rule. The refusal is fixed text and never repeats the name.</summary>
   public static string Require(string? name)
      => TryAccept(name, out string? accepted) ? accepted : throw new ArgumentException("A server's name is 1 to 64 characters of visible text without square brackets, and is not shaped like a server identifier.", nameof(name));



   /// <summary>
   /// Proposes a name for the server at an address, and the proposal always passes the rule. It is the host, by the
   /// ASCII name the connection is made to (<see cref="QqqOrigin.Of"/>), without the brackets of an IPv6 literal and
   /// cut at <see cref="MaximumCharacters"/>. A host shaped like a server identifier or its beginning (eight
   /// hexadecimal digits, for one) gets the word "Server" in front, which makes it a name and keeps the host readable;
   /// an address that has no host to name is proposed as "Server" alone. The operator can change the proposal to any
   /// name that passes.
   /// </summary>
   public static string Propose(Uri address)
   {
      ArgumentNullException.ThrowIfNull(address);
      string host = (QqqOrigin.Of(address)?.Host ?? "").Trim('[', ']');
      return TryAccept(Cut(host, MaximumCharacters), out string? proposed) ? proposed : Require(Fallback + " " + Cut(host, MaximumCharacters - Fallback.Length - 1));
   }



   /// <summary>The text, or its beginning when it is longer than the limit. A host's ASCII name holds no surrogate pair, so the cut cannot split one.</summary>
   private static string Cut(string text, int limit) => text.Length > limit ? text[..limit] : text;



   /// <summary>True when the text is made only of whole code points that are shown as themselves.</summary>
   private static bool IsPrintable(string name)
   {
      for(int index = 0; index < name.Length;)
      {
         if(Rune.DecodeFromUtf16(name.AsSpan(index), out Rune rune, out int consumed) != OperationStatus.Done || !ServerText.IsShown(rune))
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
