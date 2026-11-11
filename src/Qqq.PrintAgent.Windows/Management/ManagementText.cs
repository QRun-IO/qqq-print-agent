/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The form of free text in a typed member of the management pipe: an error's message, and the identity values a
/// server supplied. Which characters are shown is Core's one definition (<see cref="ServerText.IsShown"/>); the rest of
/// the rule is the contract's own, because a wire member is a value and not a piece of a sentence:
/// <list type="bullet">
/// <item>Every code point Core's definition does not show becomes a space, and a surrogate without its partner becomes U+FFFD. That is about a string the service or a client already holds. On the wire such a surrogate can only be written as an escape (<c>\ud800</c> on its own), which is not text and never reaches this rule: <see cref="ManagementJson"/> does not read an envelope whose own strings hold one, nor a record that holds one in a value.</item>
/// <item>The text is cut at the limit exactly, never inside a surrogate pair, and nothing is added to say that it was cut. A value is at most the limit, whatever it held.</item>
/// <item>Applying the rule to its own result changes nothing, so the reader of a reply applies it again without harm.</item>
/// <item>No words stand in for a value that is missing. A member that may be absent stays absent, and a member that must say something refuses to be built from nothing.</item>
/// </list>
/// </summary>
internal static class ManagementText
{
   /// <summary>
   /// Makes text printable and cuts it at the limit, in UTF-16 units. A code point that would pass the limit is left
   /// out whole and reading stops there, so the result is never longer than the limit, never ends in half a surrogate
   /// pair, and costs the same for a value of any length.
   /// </summary>
   internal static string Bounded(string value, int limit)
   {
      StringBuilder shown = new(Math.Min(value.Length, limit));
      foreach(Rune rune in value.EnumerateRunes())
      {
         if(shown.Length + rune.Utf16SequenceLength > limit)
         {
            break;
         }
         shown.Append(ServerText.IsShown(rune) ? rune.ToString() : " ");
      }
      return shown.ToString();
   }



   /// <summary>
   /// An error's message as the wire carries it: printable and at most <see cref="ManagementProtocol.MaximumMessageCharacters"/>
   /// UTF-16 units. A message with nothing to show (missing, empty, or only spaces once it is printable) is refused:
   /// a failure with no words for the operator is a mistake in whoever built it, and no stand-in text is sent for it.
   /// </summary>
   internal static string Message(string message)
   {
      ArgumentNullException.ThrowIfNull(message);
      string shown = Bounded(message, ManagementProtocol.MaximumMessageCharacters);
      return string.IsNullOrWhiteSpace(shown) ? throw new ArgumentException("An error's message must say something.", nameof(message)) : shown;
   }



   /// <summary>
   /// An identity value (a principal, a tenant) as the wire carries it, wherever the contract carries one: printable
   /// and at most <see cref="ManagementProtocol.MaximumIdentityCharacters"/> UTF-16 units. A value that is missing, or
   /// has nothing to show, is null, and the member is then left out of the text; it is never replaced by words a
   /// client could take for an account's name.
   /// </summary>
   internal static string? Identity(string? value)
   {
      string? shown = value is null ? null : Bounded(value, ManagementProtocol.MaximumIdentityCharacters);
      return string.IsNullOrWhiteSpace(shown) ? null : shown;
   }
}
