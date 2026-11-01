/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// Checks additive media wire compatibility and rejection boundaries before any operating-system adapter is involved.
/// </summary>
public sealed class PrinterMediaContractTests
{
   /// <summary>
   /// Provides a bounded synthetic queue default so contract tests do not depend on installed printers or physical stock.
   /// </summary>
   private static readonly PrinterMediaProfile Profile = new("opaque-default", new string('a', 64), "Default", 101600, 152400, 300, 300);



   /// <summary>
   /// Round-trips inventory and selection while verifying that legacy options still omit the optional media field.
   /// </summary>
   [Fact]
   public void OptionalMediaSelectionAndInventoryRoundTripWithoutChangingLegacyOptions()
   {
      var printer = new PrinterInfo("p", "Queue", "Driver", "Port", "READY", ["PDF", "ZPL"], MediaProfiles: [Profile]);
      string json = JsonSerializer.Serialize(printer, PrintApiClient.JsonOptions);
      PrinterInfo restored = JsonSerializer.Deserialize<PrinterInfo>(json, PrintApiClient.JsonOptions)!;
      Assert.Equal(Profile, Assert.Single(restored.MediaProfiles!));
      Assert.DoesNotContain("mediaProfile", JsonSerializer.Serialize(new PrintOptions(), PrintApiClient.JsonOptions), StringComparison.Ordinal);
      PrintOptions options = new(MediaProfile: new(Profile.Id, Profile.Revision));
      Assert.Equal(options, JsonSerializer.Deserialize<PrintOptions>(JsonSerializer.Serialize(options, PrintApiClient.JsonOptions), PrintApiClient.JsonOptions));
      PrinterMediaProfile.RequireMatch(options, [Profile]);
   }



   /// <summary>
   /// Exercises unavailable, stale and ambiguous selections plus dimension conflicts, while preserving the unselected legacy path.
   /// </summary>
   [Fact]
   public void MissingChangedDuplicateAndConflictingMediaSelectionsAreRejected()
   {
      PrintOptions options = new(MediaProfile: new(Profile.Id, Profile.Revision));
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options, null));
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options, [Profile with { Revision = new string('b', 64) }]));
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options, [Profile, Profile]));
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options with { PaperWidthMm = 101.6 }, [Profile]));
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options with { PaperHeightMm = 152.4 }, [Profile]));
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options with { MediaProfile = new(Profile.Id, new string('A', 64)) }, [Profile]));
      PrinterMediaProfile.RequireMatch(new PrintOptions(), null);
   }



   /// <summary>
   /// Checks the exact 128-character boundary and forbidden slash against matching inventory, so lookup success cannot hide malformed IDs.
   /// </summary>
   [Theory]
   [InlineData(128, false, true)]
   [InlineData(129, false, false)]
   [InlineData(128, true, false)]
   public void SelectionIdsRespectTheSharedOpaqueIdBoundary(int length, bool slash, bool accepted)
   {
      string id = new string('a', length - 4) + (slash ? "A9_/" : "A9_.");
      PrinterMediaProfile current = Profile with { Id = id };
      PrintOptions options = new(MediaProfile: new(id, current.Revision));
      if(accepted)
      {
         PrinterMediaProfile.RequireMatch(options, [current]);
      }
      else
      {
         Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options, [current]));
      }
   }



   /// <summary>
   /// Accepts maximum legal names and inventory sizes, then rejects single-step overflows and empty names.
   /// </summary>
   [Fact]
   public void CurrentProfileNamesAndInventoryHaveBoundedContractSizes()
   {
      PrinterMediaProfile current = Profile with { Name = new string('a', 512) };
      PrintOptions options = new(MediaProfile: new(current.Id, current.Revision));
      PrinterMediaProfile[] profiles = Enumerable.Range(0, 32).Select(index => current with { Id = $"p-{index}" }).ToArray();
      profiles[0] = current;
      PrinterMediaProfile.RequireMatch(options, profiles);
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options, [.. profiles, current with { Id = "overflow" }]));
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options, [current with { Name = new string('a', 513) }]));
      Assert.Throws<PrinterMediaMismatchException>(() => PrinterMediaProfile.RequireMatch(options, [current with { Name = "" }]));
   }
}
