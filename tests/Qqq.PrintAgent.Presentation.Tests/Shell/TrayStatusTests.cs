/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>The tray icon's texts: the worst health as the icon, a tooltip that fits Windows' 127 UTF-16 units whatever the servers are called, and a menu that lists every server.</summary>
public sealed class TrayStatusTests
{
   /// <summary>A service that answered with its stored data ready.</summary>
   private static readonly ServiceAvailability s_reached = new(ServiceAvailabilityKind.Reached, "The local print service is running.", string.Empty);



   /// <summary>The tray's texts for these servers of a service that answered.</summary>
   private static TrayStatus Tray(params ServerSnapshot[] servers)
   {
      ServerStatus[] statuses = [.. servers.Select(server => ServerStatus.From(server, [Snapshots.Printer()]))];
      return TrayStatus.From(s_reached, FooterStatus.From(s_reached, statuses), statuses, ShellFixture.Product);
   }



   /// <summary>
   /// With eight servers whose names are as long as a name may be, the tooltip still fits: it says the count in
   /// each state, the worst first, names the servers that fit, each whole with its health in words, and says how
   /// many were left out. The icon is the worst health. The menu lists all eight, each with its sentence.
   /// </summary>
   [Fact]
   public void Tray_ToolTip_FitsTheLimit_SaysHowManyAreLeftOut()
   {
      ServerSnapshot[] servers = [.. Enumerable.Range(1, 8).Select(number => Snapshots.Server(number, new string((char)('a' + number), 64), health: number == 3 ? ServerSnapshot.Red : ServerSnapshot.Green))];

      TrayStatus tray = Tray(servers);

      Assert.Equal(127, TrayStatus.MaximumToolTipCharacters);
      Assert.Equal("8 servers: 1 needs attention, 7 ready · " + new string('b', 64) + ": Ready · +7 more", tray.ToolTip);
      Assert.True(tray.ToolTip.Length <= 127);
      Assert.Equal("red", tray.IconName);
      Assert.Equal("8 servers: 1 needs attention, 7 ready", tray.StatusLine);
      Assert.Equal(8, tray.ServerLines.Count);
      Assert.Equal(new string('b', 64) + ": Connected and ready", tray.ServerLines[0]);
      Assert.Equal(new string('d', 64) + ": Disconnected or needs attention", tray.ServerLines[2]);
   }



   /// <summary>Servers with short names are all in the tooltip, in the tree's order, and nothing is said to be left out.</summary>
   [Fact]
   public void Tray_ToolTip_NamesEveryServerThatFits()
   {
      TrayStatus tray = Tray(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench", health: ServerSnapshot.Orange, transport: "CONNECTING"), Snapshots.Server(3, "Dock", health: "PURPLE"));

      Assert.Equal("3 servers: 1 unknown, 1 not ready, 1 ready · Warehouse: Ready · Bench: Not ready · Dock: Unknown", tray.ToolTip);
      Assert.Equal("unknown", tray.IconName);
      Assert.Equal(["Warehouse: Connected and ready", "Bench: Waiting for the server connection", "Dock: State not known to this version · update the companion"], tray.ServerLines);
   }



   /// <summary>
   /// Whatever the number of servers and the length of their names, with names that end in a character of two
   /// UTF-16 units: the tooltip is within the limit, is well-formed text (no half of a surrogate pair), does not end
   /// in a separator, lists the servers in order from the first, and the number it says were left out is the number
   /// that were.
   /// </summary>
   [Fact]
   public void Tray_ToolTip_AnyNamesAndCounts_FitsWhole_NeverCutsAPairOrLeavesASeparator()
   {
      for(int count = 2; count <= ManagementProtocol.MaximumServers; count++)
      {
         for(int length = 1; length <= 62; length++)
         {
            ServerSnapshot[] servers = [.. Enumerable.Range(1, count).Select(number => Snapshots.Server(number, new string((char)('a' + number), length) + "😀"))];

            string toolTip = Tray(servers).ToolTip;

            Assert.True(toolTip.Length <= TrayStatus.MaximumToolTipCharacters, toolTip);
            Assert.True(WellFormed(toolTip), toolTip);
            Assert.False(toolTip.TrimEnd().EndsWith('·') || toolTip.EndsWith(' '), toolTip);
            int listed = servers.TakeWhile(server => toolTip.Contains(server.Name + ": Ready", StringComparison.Ordinal)).Count();
            Assert.Equal(listed, servers.Count(server => toolTip.Contains(server.Name, StringComparison.Ordinal)));
            Assert.Equal(count - listed, LeftOut(toolTip));
            Assert.StartsWith(string.Create(CultureInfo.InvariantCulture, $"{count} servers: {count} ready"), toolTip, StringComparison.Ordinal);
         }
      }
   }



   /// <summary>With one server the tooltip is that server's sentence and the product's name, which is what 1.x showed; with none, and while the service is not available, it is the footer's sentence and the product's name.</summary>
   [Fact]
   public async Task Tray_OneServerOrNone_ReadsAsItDidBefore()
   {
      TrayStatus one = Tray(Snapshots.Server(1, "Warehouse"));
      Assert.Equal(HealthStatus.TrayToolTip(HealthStatus.From("GREEN").Label, ShellFixture.Product), one.ToolTip);
      Assert.Equal("Connected and ready — QQQ Print Agent", one.ToolTip);
      Assert.Equal("green", one.IconName);
      Assert.Equal(["Warehouse: Connected and ready"], one.ServerLines);

      TrayStatus none = Tray();
      Assert.Equal("Add a server to start printing — QQQ Print Agent", none.ToolTip);
      Assert.Equal("Add a server to start printing", none.StatusLine);
      Assert.Empty(none.ServerLines);

      using ShellFixture fixture = new();
      Assert.Equal("Checking local service… — QQQ Print Agent", fixture.Shell.Tray.ToolTip);
      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));
      await fixture.TickFailing(new IOException("The pipe is broken."));
      Assert.Equal("Local print service unavailable — QQQ Print Agent", fixture.Shell.Tray.ToolTip);
      Assert.Equal("red", fixture.Shell.Tray.IconName);
      Assert.Equal(["Warehouse: Not known while the local print service is not answering", "Bench: Not known while the local print service is not answering"], fixture.Shell.Tray.ServerLines);
   }



   /// <summary>A tooltip of one sentence drops the product's name when both do not fit, and a sentence that does not fit by itself is cut with an ellipsis, never inside a surrogate pair and with no space before the ellipsis.</summary>
   [Fact]
   public void Tray_ToolTipOfOneSentence_DropsTheProductBeforeItCuts()
   {
      Assert.Equal("Sentence — Product", TrayStatus.Sentence("Sentence", "Product"));
      Assert.Equal(new string('s', 121) + " — abc", TrayStatus.Sentence(new string('s', 121), "abc"));
      Assert.Equal(new string('s', 121), TrayStatus.Sentence(new string('s', 121), "abcd"));
      Assert.Equal(new string('s', 127), TrayStatus.Sentence(new string('s', 127), "Product"));
      Assert.Equal(new string('s', 126) + "…", TrayStatus.Sentence(new string('s', 128), "Product"));
      string pair = TrayStatus.Sentence(new string('s', 125) + "😀 and more", "Product");
      Assert.Equal(new string('s', 125) + "…", pair);
      string space = TrayStatus.Sentence(new string('s', 124) + "  and more", "Product");
      Assert.Equal(new string('s', 124) + "…", space);
   }



   /// <summary>A head that does not fit by itself is cut as a sentence is, and lists nothing after it.</summary>
   [Fact]
   public void Tray_Listing_AHeadThatDoesNotFit_IsCut()
   {
      Assert.Equal(new string('h', 126) + "…", TrayStatus.Listing(new string('h', 200), ["Warehouse: Ready"]));
      Assert.Equal("Head · +2 more", TrayStatus.Listing("Head", [new string('a', 120), "b"]));
      Assert.Equal("Head · a · b", TrayStatus.Listing("Head", ["a", "b"]));
      Assert.Equal("Head", TrayStatus.Listing("Head", []));
   }



   /// <summary>The tray's texts need all of what they are built from.</summary>
   [Fact]
   public void Tray_From_RefusesNothing()
   {
      var footer = FooterStatus.From(s_reached, []);

      Assert.Throws<ArgumentNullException>(() => TrayStatus.From(null!, footer, [], "Product"));
      Assert.Throws<ArgumentNullException>(() => TrayStatus.From(s_reached, null!, [], "Product"));
      Assert.Throws<ArgumentNullException>(() => TrayStatus.From(s_reached, footer, null!, "Product"));
      Assert.Throws<ArgumentNullException>(() => TrayStatus.From(s_reached, footer, [], null!));
   }



   /// <summary>True when every surrogate in the text is one half of a pair.</summary>
   private static bool WellFormed(string text)
   {
      for(int index = 0; index < text.Length; index++)
      {
         if(char.IsHighSurrogate(text[index]) ? index + 1 >= text.Length || !char.IsLowSurrogate(text[++index]) : char.IsLowSurrogate(text[index]))
         {
            return false;
         }
      }
      return true;
   }



   /// <summary>How many servers a tooltip says it left out in the note at its end, or none when it has no such note.</summary>
   private static int LeftOut(string toolTip)
   {
      int note = toolTip.LastIndexOf(" · +", StringComparison.Ordinal);
      return note >= 0 && toolTip.EndsWith(" more", StringComparison.Ordinal) ? int.Parse(toolTip.AsSpan(note + 4, toolTip.Length - note - 9), CultureInfo.InvariantCulture) : 0;
   }
}
