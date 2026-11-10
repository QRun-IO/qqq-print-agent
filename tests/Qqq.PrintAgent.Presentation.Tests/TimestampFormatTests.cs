/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests;

/// <summary>How service timestamps become local, culture-formatted text, and what is said when the service recorded none.</summary>
public sealed class TimestampFormatTests
{
   /// <summary>A timestamp is shown in the local zone: short for status lines, with seconds for a job's creation and for a reported instant.</summary>
   [Fact]
   public void Timestamps_AreShownInTheLocalZone()
   {
      Assert.Equal("11/10/2026 12:00", TestData.Time.Brief("2026-11-10T10:00:00Z"));
      Assert.Equal("11/10/2026 12:00:30", TestData.Time.Created("2026-11-10T10:00:30Z"));
      Assert.Equal("11/11/2026 01:59:59", TestData.Time.Full(new DateTimeOffset(2026, 11, 10, 23, 59, 59, TimeSpan.Zero)));
   }



   /// <summary>A missing or unreadable timestamp is never turned into a time: a status reads "Not yet" and a creation time "Not recorded".</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("")]
   [InlineData("yesterday-ish")]
   public void MissingTimestamps_AreSaidInWords(string? timestamp)
   {
      Assert.Equal("Not yet", TestData.Time.Brief(timestamp));
      Assert.Equal("Not recorded", TestData.Time.Created(timestamp));
   }



   /// <summary>Without a fixed culture the user's current culture formats the date, so the companion follows the Windows language; without a fixed clock the system zone is used.</summary>
   [Fact]
   public void DefaultFormat_FollowsTheCurrentCulture()
   {
      CultureInfo original = CultureInfo.CurrentCulture;
      try
      {
         CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
         TimestampFormat german = new(new FixedZoneTime(TimeSpan.Zero));

         Assert.Equal("10.11.2026 10:00", german.Brief("2026-11-10T10:00:00Z"));
         Assert.Equal("Not yet", new TimestampFormat().Brief(null));
      }
      finally
      {
         CultureInfo.CurrentCulture = original;
      }
   }
}
