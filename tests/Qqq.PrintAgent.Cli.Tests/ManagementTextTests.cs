/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The contract's own rule for free text in a typed member of the wire. Which characters are shown is Core's one
/// definition; the rest is decided here because a wire member is a value and not a piece of a sentence: a cut keeps the
/// bound exactly and adds no mark, the rule applied twice changes nothing, a member that may be absent stays absent,
/// and a message that must say something is refused when it says nothing. The characters are written as code points or
/// escapes so this file holds none of them.
/// </summary>
public sealed class ManagementTextTests
{
   /// <summary>A long emoji, as the two UTF-16 units of its surrogate pair.</summary>
   private const string Pair = "\uD83D\uDE00";



   /// <summary>
   /// Every code point Core's definition does not show becomes one space, in a message and in an identity value alike:
   /// a control character, the next-line control, the line and paragraph separators, the characters that reorder or
   /// hide text, a private-use character in either plane, an invisible tag character and an unassigned code point.
   /// </summary>
   [Theory]
   [InlineData(0x0000)]
   [InlineData(0x0007)]
   [InlineData(0x000A)]
   [InlineData(0x001B)]
   [InlineData(0x0085)]
   [InlineData(0x2028)]
   [InlineData(0x2029)]
   [InlineData(0x202A)]
   [InlineData(0x202E)]
   [InlineData(0x2066)]
   [InlineData(0x2069)]
   [InlineData(0x200B)]
   [InlineData(0x200E)]
   [InlineData(0x061C)]
   [InlineData(0xFEFF)]
   [InlineData(0xE000)]
   [InlineData(0xF0000)]
   [InlineData(0xE0001)]
   [InlineData(0x0378)]
   public void ACodePointThatIsNotShown_BecomesOneSpace(int codePoint)
   {
      string text = "a" + char.ConvertFromUtf32(codePoint) + "b";

      Assert.False(ServerText.IsShown(new Rune(codePoint)));
      Assert.Equal("a b", ManagementText.Bounded(text, 16));
      Assert.Equal("a b", ManagementText.Message(text));
      Assert.Equal("a b", ManagementText.Identity(text));
   }



   /// <summary>Visible text in any script is kept as it is, and so are the space separators other than the plain space, which are visible gaps and move nothing, the replacement character and a character outside the first plane.</summary>
   [Theory]
   [InlineData(0x0020)]
   [InlineData(0x0041)]
   [InlineData(0x00E9)]
   [InlineData(0x4E2D)]
   [InlineData(0x00A0)]
   [InlineData(0x2003)]
   [InlineData(0x3000)]
   [InlineData(0xFFFD)]
   [InlineData(0x1F600)]
   public void ACodePointThatIsShown_IsKept(int codePoint)
   {
      string text = "a" + char.ConvertFromUtf32(codePoint) + "b";

      Assert.True(ServerText.IsShown(new Rune(codePoint)));
      Assert.Equal(text, ManagementText.Bounded(text, 16));
      Assert.Equal(text, ManagementText.Message(text));
      Assert.Equal(text, ManagementText.Identity(text));
   }



   /// <summary>A surrogate without its partner is not a character; it is shown as the replacement character wherever it stands, and a whole pair is kept.</summary>
   [Fact]
   public void ASurrogateWithoutItsPartner_BecomesTheReplacementCharacter()
   {
      Assert.Equal("a\uFFFDb", ManagementText.Bounded("a\uD83Db", 16));
      Assert.Equal("a\uFFFDb", ManagementText.Bounded("a\uDE00b", 16));
      Assert.Equal("ab\uFFFD", ManagementText.Bounded("ab\uD83D", 16));
      Assert.Equal("\uFFFD\uFFFD", ManagementText.Bounded("\uDE00\uD83D", 16));
      Assert.Equal("a" + Pair + "b", ManagementText.Bounded("a" + Pair + "b", 16));
   }



   /// <summary>
   /// Text is cut at the limit exactly. Nothing is added to mark the cut, so the result is never longer than the limit;
   /// text of exactly the limit is kept whole; a surrogate pair that would straddle the limit is left out whole, and
   /// reading stops there, so nothing after it is taken to fill the room it left.
   /// </summary>
   [Fact]
   public void Text_IsCutAtTheLimitExactly_AndNeverInsideASurrogatePair()
   {
      Assert.Equal("abcde", ManagementText.Bounded("abcde", 5));
      Assert.Equal("abcde", ManagementText.Bounded("abcdef", 5));
      Assert.Equal("abcde", ManagementText.Bounded("abcde" + new string('x', 100000), 5));
      Assert.Equal("abc" + Pair, ManagementText.Bounded("abc" + Pair + "tail", 5));
      Assert.Equal("abcd", ManagementText.Bounded("abcd" + Pair + "tail", 5));
      Assert.Equal("abcd ", ManagementText.Bounded("abcd\u202Etail", 5));
      Assert.Equal("", ManagementText.Bounded("abc", 0));
      Assert.Equal("", ManagementText.Bounded(Pair, 1));
      Assert.Equal("", ManagementText.Bounded("", 5));
      Assert.Equal(new string('m', 512), ManagementText.Message(new string('m', 600)));
      Assert.Equal(new string('i', 200), ManagementText.Identity(new string('i', 300)));
      Assert.Equal(new string('i', 199), ManagementText.Identity(new string('i', 199) + Pair));
   }



   /// <summary>
   /// Applying the rule to its own result changes nothing, whatever the text held: characters that are not shown, lone
   /// surrogates, a pair on the limit and across it, and text many times the limit. That is what lets the reader of a
   /// reply apply the rule again to text the service already bounded.
   /// </summary>
   [Theory]
   [InlineData(1)]
   [InlineData(2)]
   [InlineData(5)]
   [InlineData(8)]
   [InlineData(200)]
   [InlineData(512)]
   public void TheRuleAppliedTwice_ChangesNothing(int limit)
   {
      string[] texts =
      [
         "", " ", "plain", "a\u202Eb\nc\u2028d\u200Be", "\uD83D", "ab\uDE00", Pair, "abcd" + Pair, "abc" + Pair + "tail", new string('x', limit), new string('x', limit + 1),
         new string('x', limit - 1) + Pair + "tail", new string('\u0007', limit + 3), string.Concat(Enumerable.Repeat(Pair, limit)), new string('x', 4000) + "...",
      ];

      Assert.All(texts, text =>
      {
         string once = ManagementText.Bounded(text, limit);
         Assert.Equal(once, ManagementText.Bounded(once, limit));
         Assert.True(once.Length <= limit);
         Assert.False(once.Length > 0 && char.IsHighSurrogate(once[^1]));
         Assert.Equal(ManagementText.Identity(text), ManagementText.Identity(ManagementText.Identity(text)));
      });
      Assert.Equal(ManagementText.Message("refused\u202E " + new string('x', 600)), ManagementText.Message(ManagementText.Message("refused\u202E " + new string('x', 600))));
   }



   /// <summary>A message must say something: one that is missing, empty, blank, or blank once it is printable is refused, and the refusal does not repeat it. No words stand in for it.</summary>
   [Fact]
   public void AMessageWithNothingToShow_IsRefused()
   {
      Assert.Throws<ArgumentNullException>(() => ManagementText.Message(null!));
      Assert.All(new[] { "", " ", "\t\n", "\u00A0", "\u202E\u200B", "\uE000" }, text => Assert.Throws<ArgumentException>(() => ManagementText.Message(text)));
      Assert.Equal("The server refused.", ManagementText.Message("The server refused."));
      Assert.Equal(" x ", ManagementText.Message("\nx\n"));
   }



   /// <summary>An identity value that is missing, empty, or has nothing to show once it is printable is null, never words in its place; any other value is carried, printable and bounded.</summary>
   [Fact]
   public void AnIdentityValueWithNothingToShow_IsAbsent()
   {
      Assert.Null(ManagementText.Identity(null));
      Assert.All(new[] { "", " ", "\t\n", "\u00A0", "\u202E\u200B", "\uE000" }, text => Assert.Null(ManagementText.Identity(text)));
      Assert.Equal("principal-4", ManagementText.Identity("principal-4"));
      Assert.Equal("(missing)", ManagementText.Identity("(missing)"));
      Assert.Equal(" tenant 2", ManagementText.Identity("\ttenant\u202E2"));
   }


   /// <summary>
   /// The cut is the one part of the rule a client calls too, for the members that are plain text on the wire, so it
   /// is public and refuses what cannot be a text or a limit instead of failing somewhere inside. A limit of nothing
   /// keeps nothing.
   /// </summary>
   [Fact]
   public void Bounded_IsPublic_AndRefusesNoTextAndANegativeLimit()
   {
      Assert.True(typeof(ManagementText).IsPublic);
      Assert.True(typeof(ManagementText).GetMethod(nameof(ManagementText.Bounded))!.IsPublic);
      Assert.Equal(nameof(ManagementText.Bounded), Assert.Single(typeof(ManagementText).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)).Name);
      Assert.Equal("value", Assert.Throws<ArgumentNullException>(() => ManagementText.Bounded(null!, 8)).ParamName);
      Assert.Equal("limit", Assert.Throws<ArgumentOutOfRangeException>(() => ManagementText.Bounded("text", -1)).ParamName);
      Assert.Equal("", ManagementText.Bounded("text", 0));
   }
}
