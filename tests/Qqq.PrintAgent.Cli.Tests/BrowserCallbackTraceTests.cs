/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The browser hand-off trace is written by a helper the browser starts and read back into the activity log, so it must
/// never hold the callback address or its one-time code, must never stop the hand-off when it cannot be written, and must
/// stay small. These tests run the trace rules and the handler's recording against a trace file held in memory.
/// </summary>
public sealed class BrowserCallbackTraceTests
{
   private const string CanaryCode = "canary-code-7f3a91c2d4";
   private const string CanaryState = "canary-state-55be07a1";
   private static readonly string Callback = QqqBrowserCallback.RedirectUri + "?code=" + CanaryCode + "&state=" + CanaryState;

   /// <summary>A zone five and a half hours ahead of universal time that never changes its clocks, so no local time in these tests is skipped or repeated.</summary>
   private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("QQQ test zone", new TimeSpan(5, 30, 0), "QQQ test zone", "QQQ test zone");

   /// <summary>The instant every clock in these tests starts at: 01:45:30.250 on 5 March 2026 in the test zone, plus nine tenths of a millisecond that a stamp drops.</summary>
   private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 3, 4, 20, 15, 30, 250, TimeSpan.Zero).AddTicks(9000);



   /// <summary>
   /// A callback carrying a canary authorization code and state is forwarded through the handler's real client path to a
   /// real inbox. The trace records the start with the callback's length, the steps of the forwarding and the delivery,
   /// every line stamped with the clock's local time, and neither the code, nor the state, nor the query appears anywhere in it.
   /// </summary>
   [Fact]
   public async Task DeliveredCallbackIsTracedByLengthAndFixedTextOnly()
   {
      ManualTimeProvider clock = Clock();
      var file = new FakeTraceFile();
      string name = "QqqPrintAgent.Auth.Test." + Guid.NewGuid().ToString("N");
      using QqqBrowserCallback inbox = new(name);
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
      Task<Uri> receiving = inbox.ReceiveAsync(timeout.Token);
      await QqqBrowserCallback.ForwardFromHandlerAsync(Callback, new BrowserCallbackTrace(file, clock), steps => QqqBrowserCallback.ForwardAsync(Callback, name, Environment.ProcessPath!, timeout.Token, steps));
      Assert.Equal("?code=" + CanaryCode + "&state=" + CanaryState, (await receiving).Query);
      string stamp = clock.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " ";
      Assert.All(file.Lines, line => Assert.StartsWith(stamp, line, StringComparison.Ordinal));
      Assert.StartsWith(stamp + "Protocol handler started by the browser (process " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ", session ", file.Lines[0], StringComparison.Ordinal);
      Assert.EndsWith("); callback length " + Callback.Length.ToString(CultureInfo.InvariantCulture) + ".", file.Lines[0], StringComparison.Ordinal);
      Assert.Contains(file.Lines, line => line.EndsWith("Callback address accepted: " + QqqBrowserCallback.RedirectUri + " with " + (Callback.Length - QqqBrowserCallback.RedirectUri.Length - 1).ToString(CultureInfo.InvariantCulture) + " characters of query (not logged).", StringComparison.Ordinal));
      Assert.Contains(file.Lines, line => line.Contains("Companion verified (process ", StringComparison.Ordinal));
      Assert.Equal(stamp + "Callback delivered: the companion acknowledged it.", file.Lines[^1]);
      AssertNoCanary(file);
   }



   /// <summary>
   /// When forwarding fails, the trace says so with the kind and message of the failure and of its cause, the failure is
   /// passed on unchanged, and nothing says the callback was delivered. Shown for a callback that is refused before anything
   /// connects although it carries the canary code, for an inbox that belongs to another program, and for a failure with a cause.
   /// </summary>
   [Theory]
   [InlineData("refused")]
   [InlineData("impostor")]
   [InlineData("cause")]
   public async Task FailedForwardingIsTracedWithItsReasonAndPassedOn(string failure)
   {
      var file = new FakeTraceFile();
      var trace = new BrowserCallbackTrace(file, Clock());
      string name = "QqqPrintAgent.Auth.Test." + Guid.NewGuid().ToString("N");
      using QqqBrowserCallback inbox = new(name);
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
      string forged = "https://evil.invalid/oauth/callback?code=" + CanaryCode + "&state=" + CanaryState;
      var withCause = new InvalidDataException("The active sign-in attempt did not accept the callback.", new IOException("Synthetic broken pipe."));
      Exception thrown = failure switch
      {
         "refused" => await Assert.ThrowsAsync<InvalidDataException>(() => QqqBrowserCallback.ForwardFromHandlerAsync(forged, trace, steps => QqqBrowserCallback.ForwardAsync(forged, name, Environment.ProcessPath!, timeout.Token, steps))),
         "impostor" => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => QqqBrowserCallback.ForwardFromHandlerAsync(Callback, trace, steps => QqqBrowserCallback.ForwardAsync(Callback, name, Path.Combine(Path.GetTempPath(), "Qqq.PrintAgent.exe"), timeout.Token, steps))),
         _ => await Assert.ThrowsAsync<InvalidDataException>(() => QqqBrowserCallback.ForwardFromHandlerAsync(Callback, trace, _ => Task.FromException(withCause))),
      };
      string last = file.Lines[^1];
      Assert.Contains("Callback NOT delivered: " + thrown.GetType().Name + ": " + thrown.Message, last, StringComparison.Ordinal);
      if(failure is "cause")
      {
         Assert.Same(withCause, thrown);
         Assert.EndsWith("Callback NOT delivered: InvalidDataException: The active sign-in attempt did not accept the callback. (IOException: Synthetic broken pipe.)", last, StringComparison.Ordinal);
      }
      else
      {
         Assert.EndsWith(thrown.Message, last, StringComparison.Ordinal);
      }
      Assert.DoesNotContain(file.Lines, line => line.Contains("Callback delivered", StringComparison.Ordinal));
      AssertNoCanary(file);
   }



   /// <summary>A missing callback is refused before anything is traced or forwarded.</summary>
   [Fact]
   public async Task MissingCallbackIsRefusedBeforeAnythingIsTraced()
   {
      var file = new FakeTraceFile();
      bool forwarded = false;
      _ = await Assert.ThrowsAsync<ArgumentNullException>(() => QqqBrowserCallback.ForwardFromHandlerAsync(null!, new BrowserCallbackTrace(file, Clock()), _ =>
      {
         forwarded = true;
         return Task.CompletedTask;
      }));
      Assert.False(forwarded);
      Assert.Empty(file.Lines);
   }



   /// <summary>A trace that has grown past 256 KiB is deleted before the next entry is written, so the file stays small; at the limit itself it is kept.</summary>
   [Theory]
   [InlineData(BrowserCallbackTrace.Limit, false)]
   [InlineData(BrowserCallbackTrace.Limit + 1, true)]
   public void TraceIsStartedAfreshOnlyOnceItIsOverItsLimit(long size, bool startedAfresh)
   {
      var file = new FakeTraceFile { Length = size };
      file.Lines.Add("an earlier entry");
      new BrowserCallbackTrace(file, Clock()).Write("a new entry");
      Assert.Equal(256 * 1024, BrowserCallbackTrace.Limit);
      Assert.Equal(startedAfresh ? 1 : 0, file.Deleted);
      Assert.Equal(startedAfresh ? 1 : 2, file.Lines.Count);
      Assert.EndsWith(" a new entry", file.Lines[^1], StringComparison.Ordinal);
   }



   /// <summary>A trace that cannot be written (the disk fails, or access is denied) never stops the hand-off: the entry is dropped and the forwarding still happens and succeeds.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task TraceThatCannotBeWrittenDoesNotStopTheHandOff(bool accessDenied)
   {
      var file = new FakeTraceFile { AppendFailure = accessDenied ? new UnauthorizedAccessException("Synthetic denied.") : new IOException("Synthetic disk failure.") };
      bool forwarded = false;
      await QqqBrowserCallback.ForwardFromHandlerAsync(Callback, new BrowserCallbackTrace(file, Clock()), _ =>
      {
         forwarded = true;
         return Task.CompletedTask;
      });
      Assert.True(forwarded);
      Assert.Empty(file.Lines);
   }



   /// <summary>
   /// The recent trace is the entries stamped inside the window, oldest first: older entries, lines without a timestamp and
   /// lines too short to hold one are left out, and an entry one second inside the window is included.
   /// </summary>
   [Fact]
   public void RecentTraceIsTheStampedEntriesInsideTheWindow()
   {
      ManualTimeProvider clock = Clock();
      var file = new FakeTraceFile();
      var trace = new BrowserCallbackTrace(file, clock);
      trace.Write("too old");
      clock.Advance(TimeSpan.FromMinutes(5));
      trace.Write("just inside the window");
      file.Lines.Add("no timestamp on this line at all, only text");
      file.Lines.Add("short");
      clock.Advance(TimeSpan.FromMinutes(9));
      trace.Write("recent");
      clock.Advance(TimeSpan.FromSeconds(59));
      IReadOnlyList<string> recent = trace.Recent(TimeSpan.FromMinutes(10));
      Assert.Equal(new[] { file.Lines[1], file.Lines[4] }, recent, StringComparer.Ordinal);
      Assert.EndsWith(" just inside the window", recent[0], StringComparison.Ordinal);
      Assert.EndsWith(" recent", recent[1], StringComparison.Ordinal);
   }



   /// <summary>
   /// An entry written exactly one window ago is still recent, and one millisecond later it is not. Entries are stamped
   /// to the millisecond, so the start of the window is compared at the same precision; otherwise the fraction of a
   /// millisecond the stamp drops would put such an entry outside its own window. It holds for a window of whole
   /// milliseconds and for one with a fraction of a millisecond, because the start of the window is rounded down
   /// after the window is taken off the present time, not before.
   /// </summary>
   [Theory]
   [InlineData(0)]
   [InlineData(5000)]
   public void EntryExactlyOneWindowOldIsStillRecent(long extraTicks)
   {
      TimeSpan window = TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(extraTicks);
      ManualTimeProvider clock = Clock();
      var file = new FakeTraceFile();
      var trace = new BrowserCallbackTrace(file, clock);
      trace.Write("at the start of the window");
      Assert.Equal("2026-03-05 01:45:30.250 at the start of the window", Assert.Single(file.Lines));
      clock.Advance(window);
      Assert.EndsWith(" at the start of the window", Assert.Single(trace.Recent(window)), StringComparison.Ordinal);
      clock.Advance(TimeSpan.FromMilliseconds(1));
      Assert.Empty(trace.Recent(window));
   }



   /// <summary>
   /// Stamps are read in the zone of the clock that wrote them, not in the zone the machine is set to: an entry stamped
   /// with the clock's local time is recent on any machine, and a line stamped at a time that cannot be turned into an
   /// instant at all (the first moment of the calendar, in a zone ahead of universal time) is simply not recent.
   /// </summary>
   [Fact]
   public void StampsAreReadInTheClocksOwnZone()
   {
      var file = new FakeTraceFile();
      var trace = new BrowserCallbackTrace(file, Clock());
      file.Lines.Add("2026-03-05 01:45:00.000 stamped half a minute ago in the clock's zone");
      file.Lines.Add("2026-03-04 20:15:00.000 stamped at the same reading of universal time, five and a half hours earlier");
      file.Lines.Add("0001-01-01 00:00:00.000 stamped before any instant the zone can express");
      Assert.Equal(new[] { file.Lines[0] }, trace.Recent(TimeSpan.FromMinutes(10)), StringComparer.Ordinal);
   }



   /// <summary>At most the newest 40 entries are returned, however many fall inside the window.</summary>
   [Fact]
   public void RecentTraceReturnsAtMostTheNewestForty()
   {
      ManualTimeProvider clock = Clock();
      var file = new FakeTraceFile();
      var trace = new BrowserCallbackTrace(file, clock);
      for(int entry = 1; entry <= 45; entry++)
      {
         trace.Write("entry " + entry.ToString(CultureInfo.InvariantCulture));
         clock.Advance(TimeSpan.FromSeconds(1));
      }
      IReadOnlyList<string> recent = trace.Recent(TimeSpan.FromMinutes(10));
      Assert.Equal(40, BrowserCallbackTrace.MostRecent);
      Assert.Equal(file.Lines.Skip(5), recent, StringComparer.Ordinal);
      Assert.Equal(45, trace.Recent(TimeSpan.FromMinutes(10)).Count + 5);
   }



   /// <summary>A trace that does not exist has no recent entries, and one that cannot be read (the disk fails part way, or access is denied) is reported as empty instead of failing the caller.</summary>
   [Theory]
   [InlineData("missing")]
   [InlineData("disk")]
   [InlineData("denied")]
   public void TraceThatIsMissingOrUnreadableHasNoRecentEntries(string state)
   {
      var file = new FakeTraceFile();
      var trace = new BrowserCallbackTrace(file, Clock());
      if(state is not "missing")
      {
         trace.Write("an entry");
         file.ReadFailure = state is "disk" ? new IOException("Synthetic disk failure.") : new UnauthorizedAccessException("Synthetic denied.");
      }
      Assert.Empty(trace.Recent(TimeSpan.FromMinutes(10)));
   }



   /// <summary>The trace lives in the user's own local application data, where a helper started by the browser without elevation can write it.</summary>
   [Fact]
   public void TraceFileIsInTheUsersLocalApplicationData()
      => Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QqqPrintAgent", "auth-callback.log"), QqqBrowserCallback.TracePath);



   /// <summary>
   /// A clock that starts at a fixed instant in a fixed zone. The trace stamps entries in local time and reads the stamps
   /// back, so a clock started at the real time in the machine's zone would make these tests depend on when and where
   /// they run: in the hour a daylight-saving change repeats, an entry reads as an hour newer than it is.
   /// </summary>
   private static ManualTimeProvider Clock() => new(Start, Zone);



   /// <summary>No line of the trace holds the canary code, the canary state, or a query string.</summary>
   private static void AssertNoCanary(FakeTraceFile file)
   {
      Assert.NotEmpty(file.Lines);
      Assert.All(file.Lines, line =>
      {
         Assert.DoesNotContain(CanaryCode, line, StringComparison.Ordinal);
         Assert.DoesNotContain(CanaryState, line, StringComparison.Ordinal);
         Assert.DoesNotContain("?code", line, StringComparison.Ordinal);
         Assert.DoesNotContain("evil.invalid", line, StringComparison.Ordinal);
      });
   }
}
