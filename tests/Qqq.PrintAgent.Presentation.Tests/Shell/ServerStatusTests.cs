/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>One server's state in words: the sentence beside its name, by precedence, saying no more than the snapshot does.</summary>
public sealed class ServerStatusTests
{
   /// <summary>The sentence beside a server's name, by precedence: being removed, stored state unreadable, not paired, then the footer rule of 1.x (authorization, the administrator's hold, the health's own sentence).</summary>
   [Fact]
   public void ServerStatus_Sentence_FollowsThePrecedence()
   {
      ComputerPrinter[] ready = [Snapshots.Printer()];
      string Sentence(ServerSnapshot server, ComputerPrinter[]? printers = null) => ServerStatus.From(server, printers ?? ready).Sentence;

      Assert.Equal("Being removed", Sentence(Snapshots.Server(removing: true, stateUnreadable: true, paired: false)));
      Assert.Equal("Stored data for this server cannot be read · see Connection", Sentence(Snapshots.Server(stateUnreadable: true, paired: false)));
      Assert.Equal("Not paired", Sentence(Snapshots.Server(paired: false, authorized: false, health: ServerSnapshot.Orange)));
      Assert.Equal("Authorization required", Sentence(Snapshots.Server(authorized: false, connection: "DISCONNECTED", health: ServerSnapshot.Red)));
      Assert.Equal("Dispatch disconnected by administrator", Sentence(Snapshots.Server(connection: "DISCONNECTED", paused: true, health: ServerSnapshot.Orange)));
      Assert.Equal("Dispatch paused by administrator", Sentence(Snapshots.Server(paused: true, health: ServerSnapshot.Orange)));
      Assert.Equal("Connected and ready", Sentence(Snapshots.Server()));
      Assert.Equal("Disconnected or needs attention", Sentence(Snapshots.Server(health: ServerSnapshot.Red)));
      Assert.Equal("State not known to this version · update the companion", Sentence(Snapshots.Server(health: "")));
   }



   /// <summary>
   /// An orange server is said to be connected with printers needing attention, the 1.x sentence, only when the
   /// snapshot says both: its transport is connected, and it has nothing shared or a shared printer that is not
   /// ready or is gone from Windows. Otherwise it says what the snapshot does say.
   /// </summary>
   [Fact]
   public void ServerStatus_Orange_SaysOnlyWhatTheSnapshotSays()
   {
      string Sentence(ServerSnapshot server, params ComputerPrinter[] printers) => ServerStatus.From(server, printers).Sentence;
      ServerSnapshot orange = Snapshots.Server(health: ServerSnapshot.Orange);

      Assert.Equal("Connected · printers need attention", Sentence(Snapshots.Server(health: ServerSnapshot.Orange, shared: []), Snapshots.Printer()));
      Assert.Equal("Connected · printers need attention", Sentence(orange, Snapshots.Printer(status: "OFFLINE")));
      Assert.Equal("Connected · printers need attention", Sentence(orange, Snapshots.Printer(id: "another-printer")));
      Assert.Equal("Connected · printers need attention", Sentence(orange));
      Assert.Equal("Not ready · see Connection", Sentence(orange, Snapshots.Printer(id: "another-printer", status: "OFFLINE"), Snapshots.Printer()));
      Assert.Equal("Waiting for the server connection", Sentence(Snapshots.Server(health: ServerSnapshot.Orange, transport: "RECONNECTING", shared: []), Snapshots.Printer()));
   }
}
