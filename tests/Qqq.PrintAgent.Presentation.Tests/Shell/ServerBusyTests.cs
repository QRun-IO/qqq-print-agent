/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>Busy state per server: one key of the gate for each server and one for the computer, a second command on a busy key refused out loud, and a stamp of each key's last local change.</summary>
public sealed class ServerBusyTests
{
   /// <summary>A command on one server leaves every other server and the computer free: their commands start, and only the busy server's node shows that it is busy.</summary>
   [Fact]
   public async Task Busy_OnOneServer_LeavesAnotherFree()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2), Snapshots.Server(3));

      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Signing in to Server 1. Wait for it to finish.", out IDisposable? first));

      Assert.True(fixture.Shell.Busy.IsBusy(Snapshots.Id(1)));
      Assert.False(fixture.Shell.Busy.IsBusy(Snapshots.Id(2)));
      Assert.False(fixture.Shell.Busy.IsBusy(OperationGate.Computer));
      Assert.Equal([true, false, false], fixture.Shell.Tree.Servers.Select(node => node.IsBusy));

      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(2), "Removing Server 2.", out IDisposable? second));
      Assert.True(fixture.Shell.TryBeginCommand(OperationGate.Computer, "Saving the computer's details.", out IDisposable? computer));
      Assert.Equal([true, true, false], fixture.Shell.Tree.Servers.Select(node => node.IsBusy));
      Assert.True(fixture.Shell.Tree.Computer.IsBusy);
      Assert.Null(fixture.Shell.Messages.Current);

      first.Dispose();
      Assert.Equal([false, true, false], fixture.Shell.Tree.Servers.Select(node => node.IsBusy));
      second.Dispose();
      computer.Dispose();
      Assert.Equal([false, false, false], fixture.Shell.Tree.Servers.Select(node => node.IsBusy));
      Assert.False(fixture.Shell.Tree.Computer.IsBusy);
   }



   /// <summary>
   /// A second command on a server that is busy does not start. It is refused out loud: the sentence of the command
   /// that is running is shown as a message on the page on screen, and the log says which key it was. It is not
   /// queued: when the first command ends nothing starts by itself, and the operator can then give the command
   /// again.
   /// </summary>
   [Fact]
   public async Task SecondCommandOnABusyServer_IsRefusedOutLoud()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      fixture.Shell.Select(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1)));
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Signing in to Server 1. Wait for it to finish.", out IDisposable? first));

      bool started = fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Renaming Server 1.", out IDisposable? second);

      Assert.False(started);
      Assert.Null(second);
      Assert.Equal(new FeedbackMessage("Still working", "Signing in to Server 1. Wait for it to finish.", FeedbackSeverity.Warning), fixture.Shell.Messages.Current);
      Assert.Equal(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1)), fixture.Shell.Messages.Page);
      Assert.Contains($"INFO A command on {Snapshots.Id(1)} was refused: another command is still running there.", fixture.Log.Entries, StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("Signing in", StringComparison.Ordinal) || entry.Contains("Renaming", StringComparison.Ordinal));
      Assert.True(fixture.Shell.Busy.IsBusy(Snapshots.Id(1)));

      first.Dispose();

      Assert.False(fixture.Shell.Busy.IsBusy(Snapshots.Id(1)));
      Assert.False(fixture.Shell.Tree.Servers[0].IsBusy);
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Renaming Server 1.", out IDisposable? again));
      Assert.False(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Removing Server 1.", out _));
      Assert.Equal("Renaming Server 1.", fixture.Shell.Messages.Current!.Message);
      again.Dispose();
   }



   /// <summary>A command the wizard runs shows on the Add server node, whoever entered the gate; a key the tree has no node for changes nothing.</summary>
   [Fact]
   public async Task Busy_UnderTheWizardsKey_ShowsOnAddServer()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1));

      Assert.True(fixture.Gate.TryEnter(OperationGate.Wizard, "Checking the server.", out IDisposable? wizard, out _));
      Assert.True(fixture.Gate.TryEnter(Snapshots.Id(7), "Working on a server the tree does not have.", out IDisposable? unknown, out _));

      Assert.True(fixture.Shell.Tree.AddServer.IsBusy);
      Assert.False(fixture.Shell.Tree.Servers[0].IsBusy);
      Assert.False(fixture.Shell.Tree.Computer.IsBusy);
      wizard.Dispose();
      unknown.Dispose();
      Assert.False(fixture.Shell.Tree.AddServer.IsBusy);
   }



   /// <summary>
   /// The gate can change on another thread than the window's: a command's hold may be released there, and a
   /// command may enter there. The tree is not touched on that thread, so no node tells its bindings anything
   /// there. The gate itself is right at once. The log is told once, with the key and without the holder's
   /// sentence. The next read shows on every node what the gate says: the server's, the Computer node and the Add
   /// server node.
   /// </summary>
   [Fact]
   public async Task Busy_ChangedOnAnotherThread_IsNotShownThere_TheNextReadShowsIt()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1));
      int here = Environment.CurrentManagedThreadId;
      List<int> raisedOn = [];
      foreach(TreeNode node in fixture.Shell.Tree.Nodes)
      {
         node.PropertyChanged += (_, _) =>
         {
            lock(raisedOn)
            {
               raisedOn.Add(Environment.CurrentManagedThreadId);
            }
         };
      }
      Assert.True(fixture.Shell.TryBeginCommand(Snapshots.Id(1), "Signing in to Server 1. Wait for it to finish.", out IDisposable? server));
      Assert.True(fixture.Shell.TryBeginCommand(OperationGate.Computer, "Saving the computer's details.", out IDisposable? computer));
      Assert.True(fixture.Shell.Tree.Servers[0].IsBusy);
      Assert.True(fixture.Shell.Tree.Computer.IsBusy);
      Assert.Equal([here, here], raisedOn);

      OnAPoolThread(() =>
      {
         server.Dispose();
         computer.Dispose();
         Assert.True(fixture.Gate.TryEnter(OperationGate.Wizard, "Checking the server.", out _, out _));
      });

      Assert.Equal([here, here], raisedOn);
      Assert.True(fixture.Shell.Tree.Servers[0].IsBusy);
      Assert.True(fixture.Shell.Tree.Computer.IsBusy);
      Assert.False(fixture.Shell.Tree.AddServer.IsBusy);
      Assert.False(fixture.Shell.Busy.IsBusy(Snapshots.Id(1)));
      Assert.Equal([$"INFO A command's hold on {Snapshots.Id(1)} changed on another thread than the window's. The tree shows that after the next read. This is said once."],
         fixture.Log.Entries.Where(entry => entry.Contains("another thread", StringComparison.Ordinal)), StringComparer.Ordinal);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("Signing in", StringComparison.Ordinal));

      await fixture.Tick(Snapshots.Server(1));

      Assert.False(fixture.Shell.Tree.Servers[0].IsBusy);
      Assert.False(fixture.Shell.Tree.Computer.IsBusy);
      Assert.True(fixture.Shell.Tree.AddServer.IsBusy);
      Assert.All(raisedOn, thread => Assert.Equal(here, thread));
      Assert.Equal(5, raisedOn.Count);
   }



   /// <summary>A server that is busy when a snapshot first lists it is shown as busy from the start.</summary>
   [Fact]
   public async Task Busy_AServerThatAppearsWhileACommandRunsOnIt_IsShownBusy()
   {
      using ShellFixture fixture = new();
      Assert.True(fixture.Gate.TryEnter(Snapshots.Id(2), "Pairing with the new server.", out IDisposable? hold, out _));

      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));

      Assert.Equal([false, true], fixture.Shell.Tree.Servers.Select(node => node.IsBusy));
      hold.Dispose();
      Assert.Equal([false, false], fixture.Shell.Tree.Servers.Select(node => node.IsBusy));
   }



   /// <summary>
   /// Every key has a stamp of its last local change: how many snapshots the client had handed to the pipe when the
   /// change was known to have been answered. A command stamps its key when it starts and again when it ends, with
   /// the client's count read at that moment; a refused command stamps nothing. What a snapshot says about a key is
   /// held back unless the snapshot's number is strictly greater than the stamp: the snapshot with the stamp's own
   /// number was already on its way. A result that names no flight is never newer than a change. A key that was
   /// never changed is held back by nothing, and one key's change is not another's.
   /// </summary>
   [Fact]
   public async Task Stamps_FollowEveryCommand_PerKey_AndOnlyASnapshotSentLaterIsNewer()
   {
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      OperationGate gate = new();
      using ServerBusy busy = new(gate, client);
      string one = Snapshots.Id(1);
      string two = Snapshots.Id(2);
      Assert.False(busy.ChangedSince(one, flight: null));
      Assert.False(busy.ChangedSince(one, 0));

      await Snapshot(pipe, client);
      Assert.True(busy.TryBegin(one, "Working.", out IDisposable? hold, out string? refusal));
      Assert.Null(refusal);
      Assert.True(busy.ChangedSince(one, 0));
      Assert.True(busy.ChangedSince(one, 1));
      Assert.True(busy.ChangedSince(one, flight: null));
      Assert.False(busy.ChangedSince(one, 2));
      Assert.False(busy.ChangedSince(two, 1));
      Assert.False(busy.ChangedSince(two, flight: null));

      await Snapshot(pipe, client);
      Assert.False(busy.TryBegin(one, "Another.", out IDisposable? none, out refusal));
      Assert.Null(none);
      Assert.Equal("Working.", refusal);
      Assert.False(busy.ChangedSince(one, 2));

      hold.Dispose();
      Assert.True(busy.ChangedSince(one, 2));
      Assert.False(busy.ChangedSince(one, 3));
      Assert.False(busy.ChangedSince(two, 2));
   }



   /// <summary>
   /// A change that was made without the gate is noted with its own result, and the key is stamped with the count
   /// that result carries, which the client read when the reply was read. That holds for a result whose outcome is
   /// not known too. A result of a request that was never handed to the pipe carries no count and marks nothing.
   /// A stamp never goes back: a result that is noted after a later change does not undo that change's stamp.
   /// </summary>
   [Fact]
   public async Task NoteChange_StampsTheKeyWithTheCountTheChangesOwnResultCarries()
   {
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      OperationGate gate = new();
      using ServerBusy busy = new(gate, client);
      string one = Snapshots.Id(1);
      string two = Snapshots.Id(2);
      string three = Snapshots.Id(3);
      await Snapshot(pipe, client);
      pipe.Answer(Replies.Success(new NoResult()));
      ServiceResult<NoResult> answered = await client.SharePrinterAsync(one, Snapshots.PrinterId, shared: true);
      await Snapshot(pipe, client);
      pipe.Fail(new IOException("The pipe is broken."));
      ServiceResult<NoResult> notKnown = await client.SharePrinterAsync(two, Snapshots.PrinterId, shared: true);
      ServiceResult<NoResult> neverSent = await client.SharePrinterAsync("not a server", Snapshots.PrinterId, shared: true);
      await Snapshot(pipe, client);

      busy.NoteChange(one, answered);
      busy.NoteChange(two, notKnown);
      busy.NoteChange(three, neverSent);

      Assert.Equal((1L, 2L, true, null), (answered.SnapshotsSent!.Value, notKnown.SnapshotsSent!.Value, notKnown.Unconfirmed, neverSent.SnapshotsSent));
      Assert.Equal((true, false), (busy.ChangedSince(one, 1), busy.ChangedSince(one, 2)));
      Assert.Equal((true, false), (busy.ChangedSince(two, 2), busy.ChangedSince(two, 3)));
      Assert.False(busy.ChangedSince(three, flight: null));

      Assert.True(busy.TryBegin(one, "Working.", out IDisposable? hold, out _));
      hold.Dispose();
      busy.NoteChange(one, answered);

      Assert.Equal((true, false), (busy.ChangedSince(one, 3), busy.ChangedSince(one, 4)));
   }



   /// <summary>It follows the gate until it is disposed, and it refuses to be built on nothing, asked about no key or told of a change without its result.</summary>
   [Fact]
   public async Task Busy_FollowsTheGate_UntilDisposed()
   {
      FakePipe pipe = new();
      ManagementClient client = new(pipe.SendAsync, new RecordingLog());
      OperationGate gate = new();
      ServerBusy busy = new(gate, client);
      pipe.Answer(Replies.Success(new NoResult()));
      ServiceResult<NoResult> changed = await client.SharePrinterAsync(Snapshots.Id(1), Snapshots.PrinterId, shared: true);

      busy.Dispose();
      Assert.True(gate.TryEnter(Snapshots.Id(1), "Working.", out IDisposable? hold, out _));

      Assert.False(busy.ChangedSince(Snapshots.Id(1), flight: null));
      Assert.True(busy.IsBusy(Snapshots.Id(1)));
      hold.Dispose();
      Assert.Equal("gate", Assert.Throws<ArgumentNullException>(() => new ServerBusy(null!, client)).ParamName);
      Assert.Equal("client", Assert.Throws<ArgumentNullException>(() => new ServerBusy(gate, null!)).ParamName);
      Assert.Throws<ArgumentException>(() => busy.ChangedSince(" ", 0));
      Assert.Throws<ArgumentException>(() => busy.NoteChange("", changed));
      Assert.Equal("changed", Assert.Throws<ArgumentNullException>(() => busy.NoteChange<NoResult>(Snapshots.Id(1), null!)).ParamName);
   }



   /// <summary>One snapshot, answered at once: the client has handed one more to the pipe.</summary>
   private static async Task Snapshot(FakePipe pipe, ManagementClient client)
   {
      pipe.Answer(Replies.Success(Snapshots.Of()));
      Assert.True((await client.SnapshotAsync()).Succeeded);
   }



   /// <summary>Runs something on a thread of the pool and comes back when it has ended, without leaving the thread the test is on.</summary>
   private static void OnAPoolThread(Action action)
   {
      using ManualResetEventSlim ended = new();
      ThreadPool.QueueUserWorkItem(_ =>
      {
         try
         {
            action();
         }
         finally
         {
            ended.Set();
         }
      });
      ended.Wait();
   }
}
