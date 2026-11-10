/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Presentation.Printers;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Printers;

/// <summary>How the printer table is ordered: which column and direction a header click selects, and how two printers compare under each column.</summary>
public sealed class PrinterSortTests
{
   private static readonly StringComparer s_text = StringComparer.Create(CultureInfo.InvariantCulture, ignoreCase: true);



   /// <summary>Orders printers with the sort and returns their identifiers, the way the window orders its rows.</summary>
   private static string Order(PrinterSort sort, params PrinterSnapshot[] printers)
      => string.Join(",", printers.OrderBy(printer => printer, Comparer<PrinterSnapshot>.Create(sort.Compare)).Select(printer => printer.PrinterId));



   /// <summary>The table starts by name ascending; clicking another column selects it ascending, clicking it again reverses it, and returning to a column starts ascending again.</summary>
   [Fact]
   public void HeaderClicks_SelectAColumnAndReverseTheActiveOne()
   {
      PrinterSort sort = new(s_text);
      Assert.Equal(("name", false), (sort.SortBy, sort.Descending));

      sort.Toggle("name");
      Assert.Equal(("name", true), (sort.SortBy, sort.Descending));
      sort.Toggle("status");
      Assert.Equal(("status", false), (sort.SortBy, sort.Descending));
      sort.Toggle("status");
      Assert.Equal(("status", true), (sort.SortBy, sort.Descending));
      sort.Toggle("name");
      Assert.Equal(("name", false), (sort.SortBy, sort.Descending));
   }



   /// <summary>Names compare by the label the table shows (the friendly name when set) ignoring case, and the direction reverses the order.</summary>
   [Fact]
   public void NameOrder_UsesTheDisplayedLabelIgnoringCase()
   {
      PrinterSort sort = new(s_text);
      PrinterSnapshot zebra = TestData.Printer(id: "a", name: "zebra");
      PrinterSnapshot laser = TestData.Printer(id: "b", name: "Windows Queue 7", displayName: "Laser");
      PrinterSnapshot badge = TestData.Printer(id: "c", name: "BADGE");

      Assert.Equal("c,b,a", Order(sort, zebra, laser, badge));
      sort.Toggle("name");
      Assert.Equal("a,b,c", Order(sort, zebra, laser, badge));
   }



   /// <summary>Each other column orders by what its cell shows: the status and server words, the location, the joined formats, and unshared before shared.</summary>
   [Theory]
   [InlineData("status", "b,a")]
   [InlineData("server", "a,b")]
   [InlineData("location", "b,a")]
   [InlineData("formats", "a,b")]
   [InlineData("shared", "b,a")]
   public void OtherColumns_OrderByTheirDisplayedValue(string column, string expected)
   {
      PrinterSort sort = new(s_text);
      PrinterSnapshot first = TestData.Printer(id: "a", status: "READY", formats: ["PDF"], exposed: true, location: "Warehouse", serverState: PrinterServerState.Approved);
      PrinterSnapshot second = TestData.Printer(id: "b", status: "PAUSED", formats: ["PDF", "ZPL"], exposed: false, location: "Dock", serverState: PrinterServerState.NotShared);

      sort.Toggle(column);

      Assert.Equal(expected, Order(sort, first, second));
   }



   /// <summary>Printers that tie on the sorted column are ordered by identifier in both directions, so equal rows never swap places between refreshes; a row without a printer compares equal to anything.</summary>
   [Fact]
   public void Ties_AreBrokenByIdentifierInBothDirections()
   {
      PrinterSort sort = new(s_text);
      PrinterSnapshot first = TestData.Printer(id: "a", name: "Same");
      PrinterSnapshot second = TestData.Printer(id: "b", name: "same");

      Assert.True(sort.Compare(first, second) < 0);
      sort.Toggle("name");
      Assert.True(sort.Compare(first, second) < 0);
      Assert.Equal(0, sort.Compare(null, second));
      Assert.Equal(0, sort.Compare(first, null));
   }



   /// <summary>Without a comparer the user's own language rules are used, which for plain letters still ignore case.</summary>
   [Fact]
   public void DefaultComparer_IgnoresCase()
   {
      PrinterSort sort = new();

      Assert.True(sort.Compare(TestData.Printer(id: "a", name: "alpha"), TestData.Printer(id: "b", name: "BETA")) < 0);
   }
}
