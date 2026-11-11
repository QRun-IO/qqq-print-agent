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
   /// With eight servers whose names are long, the tooltip still fits: it says the count in each state, the worst
   /// first, names the servers that fit, each whole with its health in words, and says how many were left out. The
   /// servers are named worst first, so the one that needs attention is the one named although it is third in the
   /// tree. The icon is the worst health. The menu lists all eight in the tree's order, each with its sentence.
   /// </summary>
   [Fact]
   public void Tray_ToolTip_FitsTheLimit_SaysHowManyAreLeftOut()
   {
      ServerSnapshot[] servers = [.. Enumerable.Range(1, 8).Select(number => Snapshots.Server(number, new string((char)('a' + number), 40), health: number == 3 ? ServerSnapshot.Red : ServerSnapshot.Green))];

      TrayStatus tray = Tray(servers);

      Assert.Equal(127, TrayStatus.MaximumToolTipCharacters);
      Assert.Equal("8 servers: 1 needs attention, 7 ready · " + new string('d', 40) + ": Needs attention · +7 more", tray.ToolTip);
      Assert.True(tray.ToolTip.Length <= 127);
      Assert.Equal("red", tray.IconName);
      Assert.Equal("8 servers: 1 needs attention, 7 ready", tray.StatusLine);
      Assert.Equal(8, tray.ServerLines.Count);
      Assert.Equal(new string('b', 40) + ": Connected and ready", tray.ServerLines[0]);
      Assert.Equal(new string('d', 40) + ": Disconnected or needs attention", tray.ServerLines[2]);
   }



   /// <summary>
   /// When not even the first server fits after the counts, as with eight names as long as a name may be, the
   /// tooltip is the counts, which say how many servers there are, and the product's name. It does not say "+8
   /// more", which after a count of eight servers would read as eight further ones.
   /// </summary>
   [Fact]
   public void Tray_ToolTip_WhenNoServerFits_IsTheCounts_WithNoNoteOfMore()
   {
      ServerSnapshot[] servers = [.. Enumerable.Range(1, 8).Select(number => Snapshots.Server(number, new string((char)('a' + number), 64),
         health: number % 3 == 0 ? ServerSnapshot.Red : number % 3 == 1 ? ServerSnapshot.Green : "PURPLE"))];

      TrayStatus tray = Tray(servers);

      Assert.Equal("8 servers: 2 need attention, 3 unknown, 3 ready — QQQ Print Agent", tray.ToolTip);
      Assert.DoesNotContain("more", tray.ToolTip, StringComparison.Ordinal);
      Assert.Equal(8, tray.ServerLines.Count);
   }



   /// <summary>Servers with short names are all in the tooltip, the worst first and servers in the same state in the tree's order; nothing is said to be left out, and the product's name ends it because there is room. The menu keeps the tree's order.</summary>
   [Fact]
   public void Tray_ToolTip_NamesEveryServerThatFits()
   {
      TrayStatus tray = Tray(Snapshots.Server(1, "A"), Snapshots.Server(2, "B", health: ServerSnapshot.Orange, transport: "CONNECTING"), Snapshots.Server(3, "C", health: "PURPLE"),
         Snapshots.Server(4, "D", health: ServerSnapshot.Red));

      Assert.Equal("4 servers: 1 needs attention, 1 unknown, 1 not ready, 1 ready · D: Needs attention · C: Unknown · B: Not ready · A: Ready", tray.ToolTip);
      Assert.Equal("red", tray.IconName);
      Assert.Equal(["A: Connected and ready", "B: Waiting for the server connection", "C: State not known to this version · update the companion", "D: Disconnected or needs attention"], tray.ServerLines);
      Assert.Equal("3 servers: 1 not ready, 2 ready · Bench: Not ready · Warehouse: Ready · Yard: Ready — QQQ Print Agent",
         Tray(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench", health: ServerSnapshot.Orange, transport: "CONNECTING"), Snapshots.Server(3, "Yard")).ToolTip);
   }



   /// <summary>
   /// Two of the tray's texts are equal when they say the same, whatever list instance holds the servers' lines,
   /// and differ when any one line does. So the shell tells a view about the tray only when something it shows
   /// has changed: not for a snapshot that says the same as the last one, and for one in which only a server's
   /// sentence in the menu is different.
   /// </summary>
   [Fact]
   public async Task Tray_IsRaisedOnlyWhenWhatItSaysChanged()
   {
      TrayStatus tray = Tray(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));
      Assert.Equal(tray, Tray(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench")));
      Assert.Equal(tray.GetHashCode(), Tray(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench")).GetHashCode());
      Assert.NotEqual(tray, tray with { ServerLines = ["Warehouse: Connected and ready", "Bench: Something else"] });
      Assert.NotEqual(tray, tray with { ServerLines = ["Warehouse: Connected and ready"] });
      Assert.NotEqual(tray, tray with { IconName = "red" });
      Assert.NotEqual(tray, tray with { ToolTip = "Another tooltip" });
      Assert.NotEqual(tray, tray with { StatusLine = "Another line" });

      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench", health: ServerSnapshot.Orange, transport: "CONNECTING"));
      List<string?> raised = [];
      fixture.Shell.PropertyChanged += (_, change) => raised.Add(change.PropertyName);

      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench", health: ServerSnapshot.Orange, transport: "CONNECTING"));

      Assert.Equal([nameof(ShellViewModel.Snapshot)], raised);

      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench", health: ServerSnapshot.Orange));

      Assert.Equal([nameof(ShellViewModel.Snapshot), nameof(ShellViewModel.Snapshot), nameof(ShellViewModel.Tray)], raised);
      Assert.Equal("Bench: Not ready · see Connection", fixture.Shell.Tray.ServerLines[1]);
   }



   /// <summary>While printing is paused for an update the tray says so, with one server and with several, and its icon is not the green one.</summary>
   [Fact]
   public async Task Tray_WhileAnUpdatePausesPrinting_SaysSo()
   {
      using ShellFixture fixture = new();

      await fixture.Tick(new ServiceSnapshot(Snapshots.Computer(update: new UpdateState(true)), [Snapshots.Server(1, "Warehouse")]));

      Assert.Equal("Printing paused while an update is installed — QQQ Print Agent", fixture.Shell.Tray.ToolTip);
      Assert.Equal("Printing paused while an update is installed", fixture.Shell.Tray.StatusLine);
      Assert.Equal("orange", fixture.Shell.Tray.IconName);
      Assert.Equal(["Warehouse: Connected and ready"], fixture.Shell.Tray.ServerLines);

      await fixture.Tick(new ServiceSnapshot(Snapshots.Computer(update: new UpdateState(true)), [Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench")]));

      Assert.Equal("Printing paused while an update is installed · Warehouse: Ready · Bench: Ready — QQQ Print Agent", fixture.Shell.Tray.ToolTip);
   }



   /// <summary>
   /// Whatever the number of servers and the length of their names, with names that end in a character of two
   /// UTF-16 units: the tooltip is within the limit, is well-formed text (no half of a surrogate pair), does not end
   /// in a separator, lists the servers in order from the first, and the number it says were left out is the number
   /// that were. When none is named it says nothing of more and is the counts and the product's name; when all are
   /// named the product's name ends it exactly when there is room.
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
            string head = string.Create(CultureInfo.InvariantCulture, $"{count} servers: {count} ready");
            string all = head + string.Concat(servers.Select(server => " · " + server.Name + ": Ready"));
            Assert.StartsWith(head, toolTip, StringComparison.Ordinal);
            Assert.Equal(listed == 0 || listed == count ? 0 : count - listed, LeftOut(toolTip));
            Assert.Equal(listed == 0, string.Equals(toolTip, head + " — QQQ Print Agent", StringComparison.Ordinal));
            Assert.Equal(listed == count, toolTip.StartsWith(all, StringComparison.Ordinal));
            Assert.Equal(listed == count && all.Length + 18 <= TrayStatus.MaximumToolTipCharacters, string.Equals(toolTip, all + " — QQQ Print Agent", StringComparison.Ordinal));
         }
      }
   }



   /// <summary>With one server the tooltip is that server's sentence and the product's name, which for a green server is what 1.x showed; with none, and while the service is not available, it is the footer's sentence and the product's name.</summary>
   [Fact]
   public async Task Tray_OneServerOrNone_IsTheFootersSentenceAndTheProduct()
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



   /// <summary>
   /// A head that does not fit by itself is cut as a sentence is, and lists nothing after it. When no entry fits the
   /// text is the head as a sentence, with the product's name if there is room and no note of what was left out.
   /// When only some fit, the note says how many did not. When all fit, the product's name ends the text if there
   /// is room for it.
   /// </summary>
   [Fact]
   public void Tray_Listing_AHeadThatDoesNotFit_IsCut()
   {
      Assert.Equal(new string('h', 126) + "…", TrayStatus.Listing(new string('h', 200), ["Warehouse: Ready"], "Product"));
      Assert.Equal("Head — Product", TrayStatus.Listing("Head", [new string('a', 120), "b"], "Product"));
      Assert.Equal(new string('h', 120), TrayStatus.Listing(new string('h', 120), ["a: Ready", "b: Ready"], "Product"));
      Assert.Equal("Head · " + new string('a', 100) + " · +1 more", TrayStatus.Listing("Head", [new string('a', 100), new string('b', 100)], "Product"));
      Assert.Equal("Head · a · b — Product", TrayStatus.Listing("Head", ["a", "b"], "Product"));
      Assert.Equal("Head · a · b", TrayStatus.Listing("Head", ["a", "b"], new string('p', 113)));
      Assert.Equal("Head · a · b — " + new string('p', 112), TrayStatus.Listing("Head", ["a", "b"], new string('p', 112)));
      Assert.Equal("Head — Product", TrayStatus.Listing("Head", [], "Product"));
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
