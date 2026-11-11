/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What a server's name may be (ADR-0004, "Server names"): the one rule the service and the companion both call. It
/// says which part of the rule a name breaks, so the companion can tell the operator; the contract enforces it in the
/// arguments of <c>servers.add</c> and <c>servers.rename</c>, which cannot be built with a name that breaks it, so a
/// request that gives one is refused as an invalid request when its arguments are read; and it proposes a name from a
/// server's address that always passes. The characters are written as escapes so this file holds none of them.
/// </summary>
public sealed class ServerNameTests
{
   /// <summary>The identifier the shape examples are cut from.</summary>
   private const string Identifier = ManagementSamples.ServerId;



   /// <summary>
   /// A name of 1 to 64 UTF-16 units of visible text is accepted and held as given, in any script, with spaces inside
   /// it, with punctuation other than square brackets, and with a character outside the first plane. Hexadecimal text
   /// is a name as long as it could not be taken for an identifier: fewer than eight digits, digits that run on where
   /// an identifier has its first hyphen, a space where the hyphen would be, and text longer than an identifier.
   /// </summary>
   [Theory]
   [InlineData("Warehouse")]
   [InlineData("W")]
   [InlineData("Warehouse east")]
   [InlineData("print.example.com")]
   [InlineData("print.example.com:8443")]
   [InlineData("Caf\u00E9 \u4E2D\u6587")]
   [InlineData("Dock\u00A02")]
   [InlineData("Dock \uD83D\uDE00")]
   [InlineData("Line (2) {west} <a> \"b\" 'c' #1 & co.")]
   [InlineData("(missing)")]
   [InlineData("3f2b8c1")]
   [InlineData("deadbee")]
   [InlineData("deadbeef1")]
   [InlineData("deadbeefcafe")]
   [InlineData("deadbeeg")]
   [InlineData("3f2b8c1e 5d4a")]
   [InlineData("3f2b8c1e_5d4a")]
   [InlineData("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f0")]
   [InlineData("Server 3f2b8c1e")]
   public void AName_OfVisibleTextThatIsNotAnIdentifier_IsAccepted(string name)
   {
      Assert.Equal(ServerNameProblem.None, ServerName.Check(name));
      Assert.True(ServerName.TryAccept(name, out string? accepted));
      Assert.Equal(name, accepted);
      Assert.Equal(name, ServerName.Require(name));
      Assert.Equal(name, new ServersRenameArgs(name).Name);
      Assert.Equal(name, new ServersAddArgs(ManagementSamples.Address, name, acceptCertificateException: false, ManagementSamples.SessionCredential).Name);
   }



   /// <summary>White space around a name is removed before it is judged and is not part of the stored name: a name of exactly 64 units stays one with spaces around it, and line breaks at its ends go with the spaces.</summary>
   [Fact]
   public void WhiteSpaceAroundAName_IsRemoved()
   {
      string longest = new('n', 64);

      Assert.Equal(ServerNameProblem.None, ServerName.Check("  padded \t"));
      Assert.Equal("padded", ServerName.Require("  padded \t"));
      Assert.Equal("Warehouse east", ServerName.Require("\r\nWarehouse east\n"));
      Assert.Equal(longest, ServerName.Require("   " + longest + "   "));
      Assert.Equal(longest, new ServersRenameArgs(" " + longest + " ").Name);
      Assert.Equal("padded", new ServersAddArgs(ManagementSamples.Address, " padded ", acceptCertificateException: true, ManagementSamples.SessionCredential).Name);
      Assert.Equal(64, ServerName.MaximumCharacters);
   }



   /// <summary>A name is refused when it is missing, empty or blank, or longer than 64 units once trimmed. The refusal is fixed text and never repeats the name.</summary>
   [Theory]
   [InlineData(null, ServerNameProblem.Empty)]
   [InlineData("", ServerNameProblem.Empty)]
   [InlineData("   ", ServerNameProblem.Empty)]
   [InlineData("\t\r\n", ServerNameProblem.Empty)]
   [InlineData("nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn", ServerNameProblem.TooLong)]
   [InlineData("  nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn  ", ServerNameProblem.TooLong)]
   public void AName_ThatIsEmptyOrTooLong_IsRefused(string? name, ServerNameProblem problem)
   {
      Assert.Equal(problem, ServerName.Check(name));
      Assert.False(ServerName.TryAccept(name, out string? accepted));
      Assert.Null(accepted);
      ArgumentException refusal = Assert.Throws<ArgumentException>(() => ServerName.Require(name));
      Assert.True(string.IsNullOrWhiteSpace(name) || !refusal.Message.Contains(name.Trim(), StringComparison.Ordinal));
      Assert.Throws<ArgumentException>(() => new ServersRenameArgs(name!));
      Assert.Throws<ArgumentException>(() => new ServersAddArgs(ManagementSamples.Address, name!, acceptCertificateException: false, ManagementSamples.SessionCredential));
   }



   /// <summary>
   /// A name is refused when it holds a code point that is not shown as itself, wherever in the name it stands: a line
   /// break or another control character, a line or paragraph separator, a character that reorders or hides text, a
   /// private-use character in either plane, an invisible tag character, or an unassigned code point. These are the
   /// code points Core's one definition of printable text does not show. (At the very ends of a name the ones that are
   /// white space go with the trimming; inside it nothing is removed and the name is refused.)
   /// </summary>
   [Theory]
   [InlineData(0x0000)]
   [InlineData(0x0009)]
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
   [InlineData(0xFEFF)]
   [InlineData(0xE000)]
   [InlineData(0xF0000)]
   [InlineData(0xE0001)]
   [InlineData(0x0378)]
   public void AName_WithACodePointThatIsNotShown_IsRefused(int codePoint)
   {
      string character = char.ConvertFromUtf32(codePoint);

      Assert.Equal(ServerNameProblem.NotPrintable, ServerName.Check("Ware" + character + "house"));
      Assert.False(ServerName.TryAccept("Ware" + character + "house", out string? accepted));
      Assert.Null(accepted);
      Assert.False(ServerName.TryAccept(character + "W" + character + "W", out _));
      Assert.False(ServerName.TryAccept("W" + character + "W" + character, out _));
      Assert.Throws<ArgumentException>(() => new ServersRenameArgs("Ware" + character + "house"));
   }



   /// <summary>A surrogate without its partner is not a character, so a name that holds one is refused, at its start, inside it and at its end; a whole pair is a character and is accepted.</summary>
   [Fact]
   public void AName_WithASurrogateWithoutItsPartner_IsRefused()
   {
      const char High = (char)0xD83D;
      const char Low = (char)0xDE00;

      Assert.Equal(ServerNameProblem.NotPrintable, ServerName.Check("Ware" + High + "house"));
      Assert.Equal(ServerNameProblem.NotPrintable, ServerName.Check("Ware" + Low + "house"));
      Assert.False(ServerName.TryAccept("Warehouse " + High, out _));
      Assert.False(ServerName.TryAccept(Low + " Warehouse", out _));
      Assert.False(ServerName.TryAccept("Ware" + High + "house", out _));
      Assert.False(ServerName.TryAccept("Ware" + Low + High + "house", out _));
      Assert.Throws<ArgumentException>(() => new ServersRenameArgs("Ware" + High + "house"));
      Assert.True(ServerName.TryAccept("Ware" + High + Low + "house", out string? accepted));
      Assert.Equal("Ware" + High + Low + "house", accepted);
   }



   /// <summary>A square bracket anywhere in a name is refused, because a name in a line that puts something in brackets could forge that line's structure; the brackets of an IPv6 address are the case a proposed name must avoid.</summary>
   [Theory]
   [InlineData("[")]
   [InlineData("]")]
   [InlineData("[::1]")]
   [InlineData("https://[fe80::1]:8443")]
   [InlineData("Warehouse [east]")]
   [InlineData("a] [ERROR] forged [b")]
   public void AName_WithASquareBracket_IsRefused(string name)
   {
      Assert.Equal(ServerNameProblem.SquareBracket, ServerName.Check(name));
      Assert.False(ServerName.TryAccept(name, out _));
      Assert.Throws<ArgumentException>(() => new ServersRenameArgs(name));
   }



   /// <summary>
   /// A name shaped like a server identifier, or like the first eight or more characters of one, is refused in either
   /// letter case and with spaces around it, because the command line's <c>--server</c> takes exactly that as an
   /// identifier or a prefix and compares without regard to letter case. Every beginning of an identifier from eight
   /// characters up to the whole of it is refused; a beginning of fewer than eight is too short to select by and is
   /// a name.
   /// </summary>
   [Fact]
   public void AName_ShapedLikeAnIdentifierOrItsBeginning_IsRefused()
   {
      string[] shaped = ["deadbeef", "DEADBEEF", "DeadBeef", "00000000", "3f2b8c1e-", "3f2b8c1e-5d4a", "3F2B8C1E-5D4A-", " 3f2b8c1e ", Identifier, Identifier.ToUpperInvariant()];

      Assert.All(shaped, name => Assert.Equal(ServerNameProblem.LikeAnIdentifier, ServerName.Check(name)));
      Assert.All(shaped, name => Assert.False(ServerName.TryAccept(name, out _)));
      Assert.All(Enumerable.Range(ServerSelector.MinimumPrefixLength, Identifier.Length - ServerSelector.MinimumPrefixLength + 1), length =>
      {
         Assert.False(ServerName.TryAccept(Identifier[..length], out _));
         Assert.False(ServerName.TryAccept(Identifier[..length].ToUpperInvariant(), out _));
         Assert.Throws<ArgumentException>(() => new ServersRenameArgs(Identifier[..length]));
      });
      Assert.All(Enumerable.Range(1, ServerSelector.MinimumPrefixLength - 1), length => Assert.True(ServerName.TryAccept(Identifier[..length], out _)));
      Assert.Equal(36, Identifier.Length);
      Assert.True(ManagementProtocol.IsServerId(Identifier));
   }



   /// <summary>
   /// A name that breaks the rule in several ways is reported under the first of them in the rule's order (empty, too
   /// long, not printable, a square bracket, shaped like an identifier), so the same name always gets the same answer
   /// and the companion has one thing to say.
   /// </summary>
   [Fact]
   public void AName_ThatBreaksTheRuleInSeveralWays_IsReportedUnderTheFirst()
   {
      Assert.Equal(ServerNameProblem.TooLong, ServerName.Check(new string('[', 65)));
      Assert.Equal(ServerNameProblem.TooLong, ServerName.Check(new string('n', 60) + "\n[x]\n" + new string('n', 60)));
      Assert.Equal(ServerNameProblem.NotPrintable, ServerName.Check("Ware\nhouse [east]"));
      Assert.Equal(ServerNameProblem.SquareBracket, ServerName.Check("[deadbeef]"));
      Assert.Equal(ServerNameProblem.LikeAnIdentifier, ServerName.Check("  deadbeef  "));
      Assert.Equal([ServerNameProblem.None, ServerNameProblem.Empty, ServerNameProblem.TooLong, ServerNameProblem.NotPrintable, ServerNameProblem.SquareBracket, ServerNameProblem.LikeAnIdentifier],
         Enum.GetValues<ServerNameProblem>());
   }



   /// <summary>
   /// A name proposed from a server's address always passes the rule. It is the host by its ASCII name, without a port,
   /// a path or anything else of the address; a host shaped like an identifier or its beginning gets "Server" in front;
   /// a long host is cut at 64 units; and an address with no host to name is proposed as "Server".
   /// </summary>
   [Theory]
   [InlineData("https://qqq.example.test/", "qqq.example.test")]
   [InlineData("https://QQQ.Example.Test:8443/app?x=1#top", "qqq.example.test")]
   [InlineData("https://operator:synthetic@qqq.example.test/", "qqq.example.test")]
   [InlineData("http://localhost:18581/", "localhost")]
   [InlineData("https://127.0.0.1/", "127.0.0.1")]
   [InlineData("https://[2001:db8::8a2e:370:7334]/", "2001:db8::8a2e:370:7334")]
   [InlineData("https://b\u00FCcher.example/", "xn--bcher-kva.example")]
   [InlineData("https://deadbeef.example/", "deadbeef.example")]
   [InlineData("https://deadbeef-cafe/", "Server deadbeef-cafe")]
   [InlineData("https://DEADBEEF-CAFE-4F6B/", "Server deadbeef-cafe-4f6b")]
   [InlineData("https://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.cccccccccccccccccccccccccccccc.example.test/", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.cc")]
   [InlineData("wss://qqq.example.test/qqq/v1/qbit/printing/device/realtime", "qqq.example.test")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback", "Server")]
   [InlineData("mailto:operator@example.test", "Server")]
   [InlineData("custom://qqq.example.test/x", "Server")]
   [InlineData("/qqq/v1/version", "Server")]
   [InlineData("https://id\u2028.example.test/", "Server")]
   public void Propose_EveryHostInTheTable_PassesCheck(string address, string expected)
   {
      string proposed = ServerName.Propose(new Uri(address, UriKind.RelativeOrAbsolute));

      Assert.Equal(expected, proposed);
      Assert.Equal(ServerNameProblem.None, ServerName.Check(proposed));
      Assert.Equal(proposed, ServerName.Require(proposed));
      Assert.Equal(proposed, new ServersRenameArgs(proposed).Name);
      Assert.True(proposed.Length <= ServerName.MaximumCharacters);
   }



   /// <summary>The host of an IPv6 address is written in square brackets, which a name may not hold; the proposal is the address without them.</summary>
   [Fact]
   public void Propose_Ipv6Literal_HasNoBrackets_AndPasses()
   {
      string proposed = ServerName.Propose(new Uri("https://[::1]:8443/"));

      Assert.Equal("::1", proposed);
      Assert.Equal(ServerNameProblem.SquareBracket, ServerName.Check("[::1]"));
      Assert.Equal(ServerNameProblem.None, ServerName.Check(proposed));
   }



   /// <summary>A host of eight hexadecimal digits is what the command line would take for the beginning of an identifier; the proposal puts "Server" in front, and a missing address is refused.</summary>
   [Fact]
   public void Propose_HostOfEightHexDigits_Passes()
   {
      string proposed = ServerName.Propose(new Uri("https://deadbeef/"));

      Assert.Equal("Server deadbeef", proposed);
      Assert.Equal(ServerNameProblem.LikeAnIdentifier, ServerName.Check("deadbeef"));
      Assert.Equal(ServerNameProblem.None, ServerName.Check(proposed));
      Assert.Throws<ArgumentNullException>(() => ServerName.Propose(null!));
   }



   /// <summary>
   /// A request that gives a name that breaks the rule is refused when its arguments are read, for <c>servers.rename</c>
   /// and for <c>servers.add</c>, which the dispatcher answers as an invalid request: the code the rule names. A request
   /// with a good name is read, and the name arrives trimmed.
   /// </summary>
   [Theory]
   [InlineData("Warehouse east", true)]
   [InlineData("  Warehouse east  ", true)]
   [InlineData("", false)]
   [InlineData("   ", false)]
   [InlineData("[::1]", false)]
   [InlineData("deadbeef", false)]
   [InlineData("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f", false)]
   [InlineData("Ware\u202Ehouse", false)]
   [InlineData("Ware\nhouse", false)]
   [InlineData("nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn", false)]
   public void ARequest_WithANameThatBreaksTheRule_IsRefusedWhenItsArgumentsAreRead(string name, bool accepted)
   {
      string quoted = JsonSerializer.Serialize(name);
      ManagementRequest rename = ManagementJson.ParseRequest("{\"v\":2,\"op\":\"servers.rename\",\"server\":\"" + Identifier + "\",\"args\":{\"name\":" + quoted + "}}").Request!;
      ManagementRequest add = ManagementJson.ParseRequest("{\"v\":2,\"op\":\"servers.add\",\"args\":{\"address\":\"https://qqq.example.test\",\"name\":" + quoted
         + ",\"acceptCertificateException\":false,\"sessionCredential\":{\"origin\":\"https://qqq.example.test\",\"sessionToken\":\"synthetic\"}}}").Request!;

      Assert.Null(ManagementOperations.Check(rename, out _));
      Assert.Null(ManagementOperations.Check(add, out _));
      Assert.Equal(accepted, ManagementJson.TryReadArguments(rename, out ServersRenameArgs? renamed));
      Assert.Equal(accepted, ManagementJson.TryReadArguments(add, out ServersAddArgs? added));
      Assert.Equal(accepted ? "Warehouse east" : null, renamed?.Name);
      Assert.Equal(accepted ? "Warehouse east" : null, added?.Name);
      Assert.Equal(ManagementErrorCode.InvalidRequest, ServerName.RefusalCode);
   }



   /// <summary>The name in the two argument records cannot be changed once they exist, so the rule cannot be walked around with a <c>with</c> expression; and a request for either operation that leaves the name out is refused like one with a bad name.</summary>
   [Fact]
   public void TheNameInTheArguments_CannotBeSetAfterwardsOrLeftOut()
   {
      Assert.Null(typeof(ServersRenameArgs).GetProperty(nameof(ServersRenameArgs.Name))!.SetMethod);
      Assert.All(typeof(ServersAddArgs).GetProperties(), member => Assert.Null(member.SetMethod));
      Assert.Single(typeof(ServersRenameArgs).GetConstructors());
      Assert.Single(typeof(ServersAddArgs).GetConstructors());
      Assert.False(ManagementJson.TryReadArguments(ManagementJson.ParseRequest("{\"v\":2,\"op\":\"servers.rename\",\"server\":\"" + Identifier + "\",\"args\":{}}").Request!, out ServersRenameArgs? _));
      Assert.False(ManagementJson.TryReadArguments(ManagementJson.ParseRequest("{\"v\":2,\"op\":\"servers.rename\",\"server\":\"" + Identifier + "\",\"args\":{\"name\":null}}").Request!, out ServersRenameArgs? _));
      Assert.False(ManagementJson.TryReadArguments(ManagementJson.ParseRequest("{\"v\":2,\"op\":\"servers.rename\",\"server\":\"" + Identifier + "\",\"args\":{\"name\":\"Warehouse\",\"address\":\"https://other.example.test\"}}").Request!, out ServersRenameArgs? _));
      Assert.Equal(new ServersRenameArgs("Warehouse"), new ServersRenameArgs(" Warehouse "));
   }
}
