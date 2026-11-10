/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests;

/// <summary>The texts shared by both tables: sortable column headers and the expand/collapse button of a row.</summary>
public sealed class TableTextTests
{
   /// <summary>A header that is not the sorted column shows its plain label and offers to sort by it.</summary>
   [Fact]
   public void InactiveHeader_ShowsItsLabelOnly()
   {
      var header = SortHeader.For("Status", "status", sortBy: "name", descending: true);

      Assert.Equal(new SortHeader("Status", "Sort by status", "Sort by status"), header);
   }



   /// <summary>The sorted column shows an arrow, says the direction in words for assistive technology, and its tooltip names the direction the next click will give.</summary>
   [Theory]
   [InlineData(false, "Created ↑", "Sort by created, ascending", "Sort by created descending")]
   [InlineData(true, "Created ↓", "Sort by created, descending", "Sort by created ascending")]
   public void ActiveHeader_ShowsDirectionInTextAndWords(bool descending, string content, string name, string toolTip)
   {
      var header = SortHeader.For("Created", "createdAt", sortBy: "createdAt", descending);

      Assert.Equal(new SortHeader(content, name, toolTip), header);
   }



   /// <summary>A header without a key is never the active column.</summary>
   [Fact]
   public void HeaderWithoutAKey_IsNeverActive()
      => Assert.Equal("Name", SortHeader.For("Name", key: null, sortBy: "name", descending: false).Content);



   /// <summary>The expand button names the row it belongs to and says whether it will expand or collapse, with a matching status word.</summary>
   [Fact]
   public void Disclosure_NamesTheRowAndTheNextAction()
   {
      Assert.Equal(new RowDisclosure("Expand printer details for Front Desk Laser", "Collapsed", "Expand printer details"), RowDisclosure.For("printer", "Front Desk Laser", expanded: false));
      Assert.Equal(new RowDisclosure("Collapse job details for Invoice 42", "Expanded", "Collapse job details"), RowDisclosure.For("job", "Invoice 42", expanded: true));
   }
}
