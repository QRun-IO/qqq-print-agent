/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>A time provider whose local time zone is a fixed offset from UTC with no daylight saving, so formatted local times are the same on every machine and on every date.</summary>
internal sealed class FixedZoneTime(TimeSpan offset) : TimeProvider
{
   /// <summary>The fixed zone used in place of the machine's own.</summary>
   public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("Test zone", offset, "Test zone", "Test zone");
}
