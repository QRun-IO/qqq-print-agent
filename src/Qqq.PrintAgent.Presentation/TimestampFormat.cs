/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// Turns the service's timestamps into the user's local time and date format. The time zone comes from a
/// <see cref="TimeProvider"/> and the culture can be fixed, so the companion shows local time in the user's own format
/// while a test gets the same text on any machine. A timestamp the service never recorded becomes plain words, never an
/// invented time.
/// </summary>
public sealed class TimestampFormat
{
   private readonly TimeProvider _time;
   private readonly CultureInfo? _culture;



   /// <summary>Uses the system time zone and the current culture unless others are given.</summary>
   public TimestampFormat(TimeProvider? time = null, CultureInfo? culture = null)
   {
      _time = time ?? TimeProvider.System;
      _culture = culture;
   }



   /// <summary>A short local date and time for status lines, or "Not yet" when the service has not reported one.</summary>
   public string Brief(string? timestamp) => TryRead(timestamp, out DateTimeOffset time) ? Local(time, "g") : "Not yet";



   /// <summary>A full local date and time for a job's creation, or "Not recorded" for the historical rows that never had one.</summary>
   public string Created(string? timestamp) => TryRead(timestamp, out DateTimeOffset time) ? Local(time, "G") : "Not recorded";



   /// <summary>A full local date and time for a moment the service reported as a value rather than text.</summary>
   public string Full(DateTimeOffset time) => Local(time, "G");



   /// <summary>Reads a service timestamp the way the service writes it, independent of the user's culture.</summary>
   private static bool TryRead(string? timestamp, out DateTimeOffset time)
      => DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);



   /// <summary>Converts to the local time zone and formats with the fixed culture, or the current one when none was fixed.</summary>
   private string Local(DateTimeOffset time, string format)
      => TimeZoneInfo.ConvertTime(time, _time.LocalTimeZone).ToString(format, _culture ?? CultureInfo.CurrentCulture);
}
