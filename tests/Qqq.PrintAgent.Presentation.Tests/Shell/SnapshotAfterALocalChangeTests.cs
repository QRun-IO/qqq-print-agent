/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>
/// What a snapshot says about a server that was changed from this window is shown only when the snapshot's request
/// was sent after the change was answered, whoever sent that request. The shell is not the only one that asks the
/// client for snapshots: the wizard has its own in flight, and the client hands a tick the snapshot that is in the
/// air. In these tests "another caller" asks the client directly, as the wizard does, and each test decides itself
/// when that snapshot is answered.
/// </summary>
public sealed class SnapshotAfterALocalChangeTests
{
   /// <summary>How many requests of each kind were sent so far.</summary>
   private static string Operations(ShellFixture fixture) => string.Join(' ', fixture.Pipe.Sent.Select(request => request.Op));



   /// <summary>Records, for every snapshot the shell shows, which keys it held back.</summary>
   private static List<string> HeldBack(ShellFixture fixture)
   {
      List<string> heldBack = [];
      fixture.Shell.SnapshotApplied += (_, applied) => heldBack.Add(string.Join(',', applied.HeldBack));
      return heldBack;
   }



   /// <summary>
   /// Another caller has a snapshot in flight. A command on one server starts and ends. The shell then ticks, and
   /// the client hands it the snapshot that is in the air, which was sent before the command: the shell sends
   /// nothing itself. What that snapshot says about the server that changed is not shown, and what it says about
   /// the other server is. The next tick's snapshot was sent after the change and is shown for both. It makes no
   /// difference whether the other caller asked as the timer does or for a snapshot sent after its own call.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task SnapshotAnotherCallerSentBeforeAChange_IsNotShownForTheServerThatChanged(bool sentAfterTheCall)
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));
      List<string> heldBack = HeldBack(fixture);
      TaskCompletionSource<ManagementReplyParse> inTheAir = fixture.Pipe.Hold();
      Task<ServiceResult<ServiceSnapshot>> another = fixture.Client.SnapshotAsync(sentAfterTheCall);
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Renaming Warehouse.", out IDisposable? rename));
      rename.Dispose();

      Task tick = fixture.Shell.RefreshAsync();
      Assert.Equal("snapshot snapshot", Operations(fixture));
      inTheAir.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Stale name", health: ServerSnapshot.Red), Snapshots.Server(2, "Bench east", health: ServerSnapshot.Orange))));
      await another;
      await tick;

      Assert.Equal("Warehouse,Bench east", fixture.ServerNames);
      Assert.Equal([HealthLevel.Green, HealthLevel.Orange], fixture.Shell.Tree.Servers.Select(node => node.Health.Level));
      Assert.Equal("2 servers: 1 not ready, 1 ready", fixture.Shell.Footer.Text);
      Assert.Equal([Snapshots.Id(1)], heldBack);

      await fixture.Tick(Snapshots.Server(1, "Warehouse east", health: ServerSnapshot.Red), Snapshots.Server(2, "Bench east", health: ServerSnapshot.Orange));

      Assert.Equal("Warehouse east,Bench east", fixture.ServerNames);
      Assert.Equal([HealthLevel.Red, HealthLevel.Orange], fixture.Shell.Tree.Servers.Select(node => node.Health.Level));
      Assert.Equal([Snapshots.Id(1), ""], heldBack);
      Assert.Equal("snapshot snapshot snapshot", Operations(fixture));
   }



   /// <summary>
   /// A change that is made without the gate, as the wizard makes its changes, is noted with its own result, which
   /// says how many snapshots had been sent when its reply was read. A snapshot that was in flight then is not shown
   /// for that server, though the note is made after the shell was handed it; the computer's part, noted the same
   /// way, is held back with the order of the servers. A result of a request that was never sent marks nothing.
   /// </summary>
   [Fact]
   public async Task ChangeMadeWithoutTheGate_IsNotedWithItsOwnResult_AndHoldsBackTheSnapshotThatWasInFlight()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"), Snapshots.Server(3, "Dock"));
      List<string> heldBack = HeldBack(fixture);
      TaskCompletionSource<ManagementReplyParse> inTheAir = fixture.Pipe.Hold();
      Task<ServiceResult<ServiceSnapshot>> another = fixture.Client.SnapshotAsync();
      Task tick = fixture.Shell.RefreshAsync();
      ServiceResult<NoResult> changed = await fixture.Change(1);
      ServiceResult<NoResult> neverSent = await fixture.Client.SharePrinterAsync("not a server", Snapshots.PrinterId, shared: true);

      fixture.Shell.Busy.NoteChange(Snapshots.Id(1), changed);
      fixture.Shell.Busy.NoteChange(OperationGate.Computer, changed);
      fixture.Shell.Busy.NoteChange(Snapshots.Id(3), neverSent);
      inTheAir.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(3, "Dock east"), Snapshots.Server(2, "Bench east"), Snapshots.Server(1, "Stale name"))));
      await another;
      await tick;

      Assert.Equal(2L, changed.SnapshotsSent);
      Assert.Null(neverSent.SnapshotsSent);
      Assert.Equal("Warehouse,Bench east,Dock east", fixture.ServerNames);
      Assert.Equal([Snapshots.Id(1) + "," + OperationGate.Computer], heldBack);

      await fixture.Tick(Snapshots.Server(3, "Dock east"), Snapshots.Server(2, "Bench east"), Snapshots.Server(1, "Warehouse east"));

      Assert.Equal("Dock east,Bench east,Warehouse east", fixture.ServerNames);
      Assert.Equal([Snapshots.Id(1) + "," + OperationGate.Computer, ""], heldBack);
   }



   /// <summary>
   /// A change whose outcome is not known has no moment at which it is known to have been carried out. Its result
   /// carries the count read when the outcome stopped being known: when the caller stopped waiting, or when the
   /// pipe failed. A snapshot that was in flight then is not shown for that server. The next one, sent after that
   /// moment, is shown whatever it says: the service may not have carried the change out, or not yet, and the
   /// window shows what the service says and nothing it assumes. A later snapshot shows the change when it is made.
   /// </summary>
   [Fact]
   public async Task ChangeWhoseOutcomeIsNotKnown_HoldsBackWhatWasInFlight_AndTheNextSnapshotIsShownAsItIs()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));
      List<string> heldBack = HeldBack(fixture);
      using CancellationTokenSource leaving = new();
      _ = fixture.Pipe.Hold();
      Task<ServiceResult<ServerSummary>> rename = fixture.Client.RenameServerAsync(Snapshots.Id(1), "Warehouse east", leaving.Token);
      TaskCompletionSource<ManagementReplyParse> inTheAir = fixture.Pipe.Hold();
      Task<ServiceResult<ServiceSnapshot>> another = fixture.Client.SnapshotAsync();
      await leaving.CancelAsync();
      ServiceResult<ServerSummary> waitEnded = await rename;
      fixture.Pipe.Fail(new IOException("The pipe is broken."));
      ServiceResult<ReconnectResult> pipeFailed = await fixture.Client.ReconnectAsync(Snapshots.Id(2));

      fixture.Shell.Busy.NoteChange(Snapshots.Id(1), waitEnded);
      fixture.Shell.Busy.NoteChange(Snapshots.Id(2), pipeFailed);
      Task tick = fixture.Shell.RefreshAsync();
      inTheAir.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "In flight", health: ServerSnapshot.Red), Snapshots.Server(2, "In flight", health: ServerSnapshot.Red))));
      await another;
      await tick;

      Assert.True(waitEnded.Unconfirmed && pipeFailed.Unconfirmed);
      Assert.Equal("Warehouse,Bench", fixture.ServerNames);
      Assert.Equal([Snapshots.Id(1) + "," + Snapshots.Id(2)], heldBack);

      await fixture.Tick(Snapshots.Server(1, "Warehouse", health: ServerSnapshot.Orange), Snapshots.Server(2, "Bench"));

      Assert.Equal([HealthLevel.Orange, HealthLevel.Green], fixture.Shell.Tree.Servers.Select(node => node.Health.Level));
      Assert.Equal([Snapshots.Id(1) + "," + Snapshots.Id(2), ""], heldBack);

      await fixture.Tick(Snapshots.Server(1, "Warehouse east"), Snapshots.Server(2, "Bench"));

      Assert.Equal("Warehouse east,Bench", fixture.ServerNames);
   }



   /// <summary>
   /// A read that was skipped names no flight and is not a snapshot: it changes nothing, also for a server that was
   /// changed, and it uses nothing up. The next snapshot that is sent is newer than the change and is shown.
   /// </summary>
   [Fact]
   public async Task SkippedRead_AfterAChange_ChangesNothing_AndTheNextSnapshotIsShown()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      List<string> heldBack = HeldBack(fixture);
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Renaming Warehouse.", out IDisposable? rename));
      rename.Dispose();
      fixture.Pipe.HoldEverything();
      Task<ServiceResult<NoResult>>[] inFlight = [.. Enumerable.Range(0, ManagementClient.MaximumRequestsInFlight).Select(index => fixture.Client.AcknowledgeJobAsync(Snapshots.Id(1), "job-" + index))];
      int sent = fixture.Pipe.Sent.Count;

      await fixture.Shell.RefreshAsync();

      Assert.Equal(sent, fixture.Pipe.Sent.Count);
      Assert.Empty(heldBack);
      Assert.Equal("Warehouse", fixture.ServerNames);

      fixture.Pipe.Held[0].SetResult(Replies.Success(new NoResult()));
      await inFlight[0];
      Task tick = fixture.Shell.RefreshAsync();
      fixture.Pipe.Held[^1].SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse east"))));
      await tick;

      Assert.Equal("Warehouse east", fixture.ServerNames);
      Assert.Equal([""], heldBack);
   }



   /// <summary>
   /// A server the window does not show yet is not shown from a snapshot that is older than its change either.
   /// The wizard adds a server and changes it, and notes that; a snapshot that was in flight then lists the server
   /// as it was at some moment before. The tree does not take that entry: the server is left out, and is shown,
   /// and its page opened as the wizard asked, from the first snapshot that was sent after the change.
   /// </summary>
   [Fact]
   public async Task ServerTheWindowDoesNotShowYet_IsLeftOutOfASnapshotOlderThanItsChange()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"));
      List<string> heldBack = HeldBack(fixture);
      TaskCompletionSource<ManagementReplyParse> inTheAir = fixture.Pipe.Hold();
      Task<ServiceResult<ServiceSnapshot>> another = fixture.Client.SnapshotAsync();
      fixture.Shell.Busy.NoteChange(Snapshots.Id(3), await fixture.Change(3));
      fixture.Shell.WizardEnded(PageKey.Server(Snapshots.Id(3)));

      Task tick = fixture.Shell.RefreshAsync();
      inTheAir.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse east"), Snapshots.Server(3, "Dock", paired: false))));
      await another;
      await tick;

      Assert.Equal("Warehouse east", fixture.ServerNames);
      Assert.Equal([Snapshots.Id(3)], heldBack);
      Assert.Equal("Connected and ready", fixture.Shell.Footer.Text);
      Assert.NotEqual(PageKey.Server(Snapshots.Id(3)), fixture.Shell.Selected);

      await fixture.Tick(Snapshots.Server(1, "Warehouse east"), Snapshots.Server(3, "Dock"));

      Assert.Equal("Warehouse east,Dock", fixture.ServerNames);
      Assert.Equal("Connected and ready", fixture.Shell.Tree.Servers[1].StatusText);
      Assert.Equal(PageKey.Server(Snapshots.Id(3)), fixture.Shell.Selected);
      Assert.Equal([Snapshots.Id(3), ""], heldBack);
   }



   /// <summary>
   /// A snapshot that is not newer than a server's change cannot make that server go. Another caller's snapshot is
   /// in flight, a command on the server whose page is on screen starts and ends, and the snapshot the tick is then
   /// handed does not list that server. The server stays, its page stays on screen, nothing says that a server was
   /// removed, in a message or in the log, and a failure reported for its page is shown there as any other. Only a
   /// snapshot that was sent after the change and does not list the server means that it has gone.
   /// </summary>
   [Fact]
   public async Task SnapshotOlderThanAServersChange_ThatDoesNotListIt_NeverSaysTheServerWasRemoved()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "Warehouse"), Snapshots.Server(2, "Bench"));
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(2));
      fixture.Shell.Select(jobs);
      List<string> heldBack = HeldBack(fixture);
      TaskCompletionSource<ManagementReplyParse> inTheAir = fixture.Pipe.Hold();
      Task<ServiceResult<ServiceSnapshot>> another = fixture.Client.SnapshotAsync();
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(2), "Signing in to Bench.", out IDisposable? signIn));
      signIn.Dispose();

      Task tick = fixture.Shell.RefreshAsync();
      inTheAir.SetResult(Replies.Success(Snapshots.Of(Snapshots.Server(1, "Warehouse east"))));
      await another;
      await tick;

      Assert.Equal("Warehouse east,Bench", fixture.ServerNames);
      Assert.Equal(jobs, fixture.Shell.Selected);
      Assert.Null(fixture.Shell.Messages.Current);
      Assert.Equal([Snapshots.Id(2)], heldBack);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("no longer on this computer", StringComparison.Ordinal) || entry.Contains("not listed", StringComparison.Ordinal));
      var failure = new FeedbackMessage("Retry failed", "The job could not be retried.", FeedbackSeverity.Error);
      fixture.Shell.Messages.Show(jobs, failure);
      Assert.Equal(failure, fixture.Shell.Messages.Current);

      await fixture.Tick(Snapshots.Server(1, "Warehouse east"), Snapshots.Server(2, "Bench east"));

      Assert.Equal("Warehouse east,Bench east", fixture.ServerNames);
      Assert.Equal((jobs, failure), (fixture.Shell.Selected, fixture.Shell.Messages.Current));
      fixture.Shell.Messages.Dismiss();

      await fixture.Tick(Snapshots.Server(1, "Warehouse east"));

      Assert.Equal("Warehouse east", fixture.ServerNames);
      Assert.Equal(PageKey.Server(Snapshots.Id(1)), fixture.Shell.Selected);
      Assert.Equal("Server removed", fixture.Shell.Messages.Current!.Title);
      Assert.Contains($"INFO Server {Snapshots.Id(2)} is no longer on this computer; its page was on screen, and {PageKey.Server(Snapshots.Id(1))} is shown instead.", fixture.Log.Entries, StringComparer.Ordinal);
   }
}
