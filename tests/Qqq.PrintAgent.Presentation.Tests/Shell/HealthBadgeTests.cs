/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>Health as it is shown: a level, a glyph and words that differ for every level, the reading of the contract's health word, and which of several levels is worst.</summary>
public sealed class HealthBadgeTests
{
   /// <summary>Each health level has its own glyph and its own words, unknown included, so a state is never told by colour alone; a level this build does not list is shown as unknown.</summary>
   [Fact]
   public void HealthBadge_EveryLevel_HasItsOwnGlyphAndWords()
   {
      HealthBadge[] badges = [.. Enum.GetValues<HealthLevel>().Select(HealthBadge.For)];

      Assert.Equal(4, badges.Length);
      Assert.Equal(Enum.GetValues<HealthLevel>(), badges.Select(badge => badge.Level));
      Assert.Equal(4, badges.Select(badge => badge.GlyphName).Distinct(StringComparer.Ordinal).Count());
      Assert.Equal(4, badges.Select(badge => badge.Text).Distinct(StringComparer.Ordinal).Count());
      Assert.All(badges, badge => Assert.False(string.IsNullOrWhiteSpace(badge.GlyphName) || string.IsNullOrWhiteSpace(badge.Text)));
      Assert.Equal(new HealthBadge(HealthLevel.Green, "CheckMark", "Ready"), HealthBadge.For(HealthLevel.Green));
      Assert.Equal(new HealthBadge(HealthLevel.Orange, "Warning", "Not ready"), HealthBadge.For(HealthLevel.Orange));
      Assert.Equal(new HealthBadge(HealthLevel.Red, "Error", "Needs attention"), HealthBadge.For(HealthLevel.Red));
      Assert.Equal(new HealthBadge(HealthLevel.Unknown, "Unknown", "Unknown"), HealthBadge.For(HealthLevel.Unknown));
      Assert.Equal(HealthBadge.For(HealthLevel.Unknown), HealthBadge.For((HealthLevel)99));
   }



   /// <summary>The health words of the contract are read into four levels, and anything else is unknown; the worst of several levels is red, then unknown, then orange, then green.</summary>
   [Fact]
   public void HealthStatus_ReadsTheWord_AndRanksTheLevels()
   {
      Assert.Equal(HealthLevel.Green, HealthStatus.LevelOf(ServerSnapshot.Green));
      Assert.Equal(HealthLevel.Orange, HealthStatus.LevelOf(ServerSnapshot.Orange));
      Assert.Equal(HealthLevel.Red, HealthStatus.LevelOf(ServerSnapshot.Red));
      Assert.Equal(HealthLevel.Unknown, HealthStatus.LevelOf("PURPLE"));
      Assert.Equal(HealthLevel.Unknown, HealthStatus.LevelOf("green"));
      Assert.Equal(HealthLevel.Unknown, HealthStatus.LevelOf(null));

      Assert.Equal(HealthLevel.Green, HealthStatus.Worst([]));
      Assert.Equal(HealthLevel.Green, HealthStatus.Worst([HealthLevel.Green, HealthLevel.Green]));
      Assert.Equal(HealthLevel.Orange, HealthStatus.Worst([HealthLevel.Green, HealthLevel.Orange, HealthLevel.Green]));
      Assert.Equal(HealthLevel.Unknown, HealthStatus.Worst([HealthLevel.Orange, HealthLevel.Unknown, HealthLevel.Green]));
      Assert.Equal(HealthLevel.Unknown, HealthStatus.Worst([HealthLevel.Unknown, HealthLevel.Orange]));
      Assert.Equal(HealthLevel.Red, HealthStatus.Worst([HealthLevel.Unknown, HealthLevel.Red, HealthLevel.Orange]));
      Assert.Equal(HealthLevel.Red, HealthStatus.Worst([HealthLevel.Red, HealthLevel.Unknown]));
      Assert.Throws<ArgumentNullException>(() => HealthStatus.Worst(null!));

      Assert.Equal(["green", "orange", "red", "unknown"], Enum.GetValues<HealthLevel>().Select(HealthStatus.TrayIconName), StringComparer.Ordinal);
      Assert.Equal("unknown", HealthStatus.TrayIconName((HealthLevel)99));
   }
}
