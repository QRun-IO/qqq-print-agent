/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>Busy state per server: one key of the gate for each server and one for the computer, a second command on a busy key refused out loud, and a mark of each key's last local change.</summary>
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
   /// Every key has a mark of its last local change. A command changes its key when it starts and again when it
   /// ends; a change made without the gate is noted; a refused command changes nothing. A key that was never
   /// changed has not changed since any mark, and one key's change is not another's.
   /// </summary>
   [Fact]
   public void Marks_FollowEveryLocalChange_PerKey()
   {
      OperationGate gate = new();
      using ServerBusy busy = new(gate);
      string one = Snapshots.Id(1);
      string two = Snapshots.Id(2);
      long start = busy.Mark();
      Assert.False(busy.ChangedSince(one, start));

      Assert.True(busy.TryBegin(one, "Working.", out IDisposable? hold, out string? refusal));
      Assert.Null(refusal);
      long running = busy.Mark();
      Assert.True(busy.ChangedSince(one, start));
      Assert.False(busy.ChangedSince(one, running));
      Assert.False(busy.ChangedSince(two, start));

      Assert.False(busy.TryBegin(one, "Another.", out IDisposable? none, out refusal));
      Assert.Null(none);
      Assert.Equal("Working.", refusal);
      Assert.Equal(running, busy.Mark());

      hold.Dispose();
      Assert.True(busy.ChangedSince(one, running));
      long ended = busy.Mark();
      Assert.False(busy.ChangedSince(one, ended));

      busy.NoteChange(two);
      Assert.True(busy.ChangedSince(two, ended));
      Assert.False(busy.ChangedSince(one, ended));
      Assert.True(busy.Mark() > ended);
   }



   /// <summary>It follows the gate until it is disposed, and it refuses to be built on nothing or asked about no key.</summary>
   [Fact]
   public void Busy_FollowsTheGate_UntilDisposed()
   {
      OperationGate gate = new();
      ServerBusy busy = new(gate);
      long start = busy.Mark();

      busy.Dispose();
      Assert.True(gate.TryEnter(Snapshots.Id(1), "Working.", out IDisposable? hold, out _));

      Assert.False(busy.ChangedSince(Snapshots.Id(1), start));
      Assert.True(busy.IsBusy(Snapshots.Id(1)));
      hold.Dispose();
      Assert.Equal("gate", Assert.Throws<ArgumentNullException>(() => new ServerBusy(null!)).ParamName);
      Assert.Throws<ArgumentException>(() => busy.ChangedSince(" ", start));
      Assert.Throws<ArgumentException>(() => busy.NoteChange(""));
   }
}
