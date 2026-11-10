/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Resolving the command line's --server value. The service takes a full identifier only, so the command line turns what
/// the operator typed (an identifier, the start of one, or a name) into one server from the list, and refuses, with the
/// identifiers involved, anything that could mean more than one server or none.
/// </summary>
public sealed class ManagementServerSelectorTests
{
   /// <summary>The identifier of the server named Warehouse; it shares its first eight characters with the next one.</summary>
   private const string Warehouse = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";



   /// <summary>The identifier of the server named Test bench.</summary>
   private const string Bench = "3f2b8c1e-0000-4f6b-9a7c-0e1d2c3b4a60";



   /// <summary>The identifier of the server named Office, which no other identifier resembles.</summary>
   private const string Office = "a81d44c0-17b2-4e0f-8d6a-5c4b3a291807";



   /// <summary>Three servers in the operator's order; the last is being removed.</summary>
   private static readonly ServerSummary[] s_three =
   [
      new(Warehouse, "Warehouse", "https://qqq.example.test", 0, ServerSnapshot.Green, "Connected."),
      new(Bench, "Test bench", "https://bench.example.test", 1, ServerSnapshot.Red, "Sign-in is required."),
      new(Office, "Office", "https://office.example.test", 2, ServerSnapshot.Green, "Connected.", Removing: true),
   ];



   /// <summary>A full identifier selects its server, in any letter case and with spaces around it, even though it also begins itself.</summary>
   [Theory]
   [InlineData(Warehouse, Warehouse)]
   [InlineData(Bench, Bench)]
   [InlineData("A81D44C0-17B2-4E0F-8D6A-5C4B3A291807", Office)]
   [InlineData("  " + Warehouse + " ", Warehouse)]
   public void FullIdentifier_SelectsItsServer(string selector, string expected)
   {
      AssertSelected(expected, ServerSelector.Resolve(selector, s_three));
   }



   /// <summary>The first eight or more characters of an identifier select the server when only one identifier begins that way.</summary>
   [Theory]
   [InlineData("a81d44c0", Office)]
   [InlineData("A81D44C0-17", Office)]
   [InlineData("3f2b8c1e-5", Warehouse)]
   [InlineData("3f2b8c1e-0000-4f6b", Bench)]
   public void IdentifierPrefix_OfEightOrMoreCharacters_SelectsItsServer(string selector, string expected)
   {
      AssertSelected(expected, ServerSelector.Resolve(selector, s_three));
   }



   /// <summary>A prefix shorter than eight characters is refused as too short even when only one identifier begins with it, and the refusal lists the identifiers it begins; the command line reports it as a malformed request.</summary>
   [Theory]
   [InlineData("a81d44c", Office)]
   [InlineData("a", Office)]
   [InlineData("3f2b", Warehouse + "," + Bench)]
   public void IdentifierPrefix_ShorterThanEightCharacters_IsRefused(string selector, string beginning)
   {
      ServerSelection selection = ServerSelector.Resolve(selector, s_three);

      Assert.Equal(ServerSelectionOutcome.PrefixTooShort, selection.Outcome);
      Assert.Null(selection.ServerId);
      Assert.Equal(beginning.Split(','), selection.Candidates);
      Assert.Equal(ManagementErrorCode.InvalidRequest, selection.ErrorCode());
      Assert.Equal(8, ServerSelector.MinimumPrefixLength);
   }



   /// <summary>A prefix of eight or more characters that two identifiers share is ambiguous, and both identifiers are listed in the list's order.</summary>
   [Fact]
   public void IdentifierPrefix_SharedByTwoServers_IsAmbiguous()
   {
      ServerSelection selection = ServerSelector.Resolve("3f2b8c1e", s_three);

      Assert.Equal(ServerSelectionOutcome.Ambiguous, selection.Outcome);
      Assert.Null(selection.ServerId);
      Assert.Equal([Warehouse, Bench], selection.Candidates);
      Assert.Equal(ManagementErrorCode.ServerAmbiguous, selection.ErrorCode());
   }



   /// <summary>A server's name selects it, whatever the letter case, including a name with a space and the name of a server that is being removed.</summary>
   [Theory]
   [InlineData("Warehouse", Warehouse)]
   [InlineData("warehouse", Warehouse)]
   [InlineData("TEST BENCH", Bench)]
   [InlineData(" Office ", Office)]
   public void Name_SelectsItsServer(string selector, string expected)
   {
      AssertSelected(expected, ServerSelector.Resolve(selector, s_three));
   }



   /// <summary>A name that two servers have is refused as ambiguous with both identifiers, so the operator can name one by its identifier.</summary>
   [Fact]
   public void Name_OfTwoServers_IsAmbiguousAndListsBothIdentifiers()
   {
      ServerSummary[] servers = [.. s_three, new(Office[..35] + "9", "warehouse", "https://second.example.test", 3, ServerSnapshot.Green, "Connected.")];

      ServerSelection selection = ServerSelector.Resolve("Warehouse", servers);

      Assert.Equal(ServerSelectionOutcome.Ambiguous, selection.Outcome);
      Assert.Null(selection.ServerId);
      Assert.Equal([Warehouse, Office[..35] + "9"], selection.Candidates);
      Assert.Equal(ManagementErrorCode.ServerAmbiguous, selection.ErrorCode());
   }



   /// <summary>A value that is one server's name and the start of another's identifier could mean either, so it is ambiguous; a server that matches both ways is still one server and is selected.</summary>
   [Fact]
   public void Name_ThatIsAlsoAnotherServersPrefix_IsAmbiguous_ButOneServerMatchingTwiceIsNot()
   {
      ServerSummary[] clash = [.. s_three, new("0f0e0d0c-1111-4222-8333-444455556666", "a81d44c0", "https://clash.example.test", 3, ServerSnapshot.Green, "Connected.")];
      ServerSummary[] same = [new(Office, "a81d44c0", "https://office.example.test", 0, ServerSnapshot.Green, "Connected.")];

      ServerSelection ambiguous = ServerSelector.Resolve("a81d44c0", clash);

      Assert.Equal(ServerSelectionOutcome.Ambiguous, ambiguous.Outcome);
      Assert.Equal([Office, "0f0e0d0c-1111-4222-8333-444455556666"], ambiguous.Candidates);
      AssertSelected(Office, ServerSelector.Resolve("a81d44c0", same));
   }



   /// <summary>A value that is no identifier, no name and begins no identifier selects nothing and lists nothing. An address is not a way to select a server.</summary>
   [Theory]
   [InlineData("Shipping")]
   [InlineData("https://qqq.example.test")]
   [InlineData("ffffffff")]
   [InlineData("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f0")]
   public void UnknownValue_SelectsNothing(string selector)
   {
      ServerSelection selection = ServerSelector.Resolve(selector, s_three);

      Assert.Equal(ServerSelectionOutcome.NotFound, selection.Outcome);
      Assert.Null(selection.ServerId);
      Assert.Empty(selection.Candidates);
      Assert.Equal(ManagementErrorCode.ServerNotFound, selection.ErrorCode());
   }



   /// <summary>With no value, the only server is selected.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("   ")]
   public void Omitted_WithOneServer_SelectsIt(string? selector)
   {
      AssertSelected(Bench, ServerSelector.Resolve(selector, [s_three[1]]));
   }



   /// <summary>With no value and several servers, one must be named; the refusal lists every identifier and is a malformed request, not an ambiguous name.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData(" ")]
   public void Omitted_WithSeveralServers_IsRefusedAndListsThem(string? selector)
   {
      ServerSelection selection = ServerSelector.Resolve(selector, s_three);

      Assert.Equal(ServerSelectionOutcome.SelectorRequired, selection.Outcome);
      Assert.Null(selection.ServerId);
      Assert.Equal([Warehouse, Bench, Office], selection.Candidates);
      Assert.Equal(ManagementErrorCode.InvalidRequest, selection.ErrorCode());
   }



   /// <summary>With no servers nothing can be selected, whether or not a value was given.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("Warehouse")]
   [InlineData(Warehouse)]
   public void NoServers_SelectsNothing(string? selector)
   {
      ServerSelection selection = ServerSelector.Resolve(selector, []);

      Assert.Equal(ServerSelectionOutcome.NoServers, selection.Outcome);
      Assert.Null(selection.ServerId);
      Assert.Empty(selection.Candidates);
      Assert.Equal(ManagementErrorCode.ServerNotFound, selection.ErrorCode());
   }



   /// <summary>Requires a selection of exactly the expected server, with no error code.</summary>
   private static void AssertSelected(string expected, ServerSelection selection)
   {
      Assert.Equal(ServerSelectionOutcome.Selected, selection.Outcome);
      Assert.Equal(expected, selection.ServerId);
      Assert.Equal([expected], selection.Candidates);
      Assert.Null(selection.ErrorCode());
   }
}
