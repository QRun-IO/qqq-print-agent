/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>The commands of a server's menu: which there are, when each can be chosen, why not, and the keys that choose them.</summary>
public sealed class ServerCommandsTests
{
   /// <summary>The menu's commands and when each can be chosen: reconnect needs a pairing and a session and is not the operator's while the administrator holds the computer disconnected; sign out needs a session; the ends of the list cannot move past them. Each disabled command says why.</summary>
   [Fact]
   public void Commands_EnabledRules_AndReasons()
   {
      static string[] Reasons(ServerSnapshot server, int index, int count)
         => [.. ServerCommands.For(server, index, count).All.Select(command => command.IsEnabled == command.DisabledReason is null ? command.DisabledReason ?? "enabled" : "inconsistent")];

      Assert.Equal(["enabled", "enabled", "enabled", "enabled", "enabled", "enabled"], Reasons(Snapshots.Server(), 1, 3));
      Assert.Equal(["This computer is not paired with this server. Open Connection to pair it.", "enabled", "enabled", "enabled", "This server is already first.", "enabled"],
         Reasons(Snapshots.Server(paired: false), 0, 3));
      Assert.Equal(["Nobody is signed in to this server. Open Connection to sign in.", "Nobody is signed in to this server.", "enabled", "enabled", "enabled", "This server is already last."],
         Reasons(Snapshots.Server(signedIn: false, authorized: false), 2, 3));
      Assert.Equal(["The server's administrator has disconnected this computer. Only they can connect it again.", "enabled", "enabled", "enabled", "This is the only server.", "This is the only server."],
         Reasons(Snapshots.Server(connection: "DISCONNECTED"), 0, 1));
      Assert.Empty(ServerCommands.For(Snapshots.Server(removing: true), 0, 1).All);

      var commands = ServerCommands.For(Snapshots.Server(), 0, 2);
      Assert.Equal([ServerCommandKind.Reconnect, ServerCommandKind.SignOut, ServerCommandKind.Rename, ServerCommandKind.Remove, ServerCommandKind.MoveUp, ServerCommandKind.MoveDown],
         commands.All.Select(command => command.Kind));
      Assert.Equal(new ServerCommand(ServerCommandKind.MoveUp, "Move up", "U", "Alt+Up", false, "This server is already first."), commands.Find(ServerCommandKind.MoveUp));
      Assert.Equal(new ServerCommand(ServerCommandKind.MoveDown, "Move down", "D", "Alt+Down", true, null), commands.Find(ServerCommandKind.MoveDown));
      Assert.True(commands.Same(ServerCommands.For(Snapshots.Server(), 0, 2)));
      Assert.False(commands.Same(ServerCommands.For(Snapshots.Server(), 1, 2)));
   }



   /// <summary>Every command of a server's menu has a label and an access key that is a letter of its label and that no other command of the menu has; the four that can be run from the tree have their key.</summary>
   [Fact]
   public void Commands_EachHasALabelAndAnAccessKey_UniqueInTheMenu()
   {
      IReadOnlyList<ServerCommand> commands = ServerCommands.For(Snapshots.Server(), 0, 2).All;

      Assert.Equal(commands.Count, commands.Select(command => command.AccessKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
      Assert.All(commands, command =>
      {
         Assert.False(string.IsNullOrWhiteSpace(command.Label));
         Assert.Equal(1, command.AccessKey.Length);
         Assert.Contains(command.AccessKey, command.Label, StringComparison.OrdinalIgnoreCase);
      });
      Assert.Equal(["none", "none", "F2", "Delete", "Alt+Up", "Alt+Down"], commands.Select(command => command.Gesture ?? "none"), StringComparer.Ordinal);
      Assert.Equal(["Reconnect", "Sign out", "Rename", "Remove", "Move up", "Move down"], commands.Select(command => command.Label), StringComparer.Ordinal);
   }
}
