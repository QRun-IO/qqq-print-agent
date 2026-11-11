/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>The key that names a page.</summary>
public sealed class PageKeyTests
{
   /// <summary>A page's key names its kind and, for a server's page, a server by an identifier in the contract's one form; anything else is refused with fixed text.</summary>
   [Fact]
   public void PageKey_NamesAPage_AndRefusesWhatIsNotOne()
   {
      Assert.Equal(PageKey.ForServer(PageKind.Server, Snapshots.Id(1)), PageKey.Server(Snapshots.Id(1)));
      Assert.NotEqual(PageKey.Server(Snapshots.Id(1)), PageKey.Server(Snapshots.Id(2)));
      Assert.NotEqual(PageKey.Server(Snapshots.Id(1)), PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1)));
      Assert.Equal((PageKind.Jobs, Snapshots.Id(1)), (PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1)).Kind, PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1)).ServerId));
      Assert.Equal([PageKind.Wizard, PageKind.Computer, PageKind.Logs, PageKind.Settings], new[] { PageKey.Wizard, PageKey.Computer, PageKey.Logs, PageKey.Settings }.Select(key => key.Kind));
      Assert.All(new[] { PageKey.Wizard, PageKey.Computer, PageKey.Logs, PageKey.Settings }, key => Assert.Null(key.ServerId));
      Assert.Equal("Logs", PageKey.Logs.ToString());
      Assert.Equal("Jobs " + Snapshots.Id(1), PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1)).ToString());

      Assert.Equal("kind", Assert.Throws<ArgumentException>(() => PageKey.ForServer(PageKind.Computer, Snapshots.Id(1))).ParamName);
      Assert.Equal("kind", Assert.Throws<ArgumentException>(() => PageKey.ForServer(PageKind.Wizard, Snapshots.Id(1))).ParamName);
      ArgumentException refused = Assert.Throws<ArgumentException>(() => PageKey.Server("WAREHOUSE"));
      Assert.Equal("serverId", refused.ParamName);
      Assert.DoesNotContain("WAREHOUSE", refused.Message, StringComparison.Ordinal);
      Assert.Throws<ArgumentException>(() => PageKey.Server(Snapshots.Id(1).ToUpperInvariant()));
      Assert.Throws<ArgumentException>(() => PageKey.Server(null!));
   }
}
