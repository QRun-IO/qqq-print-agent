/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The commands of one server's menu: reconnect, sign out, rename, remove, move up and move down, each with its
/// label, its access key, whether it can be chosen and, when it cannot, why. A server that is being removed offers
/// none. Reordering is by Move up and Move down, from the menu and by Alt+Up and Alt+Down; there is no drag and drop.
/// <para>
/// A command that can be chosen is not disabled while another command runs on the same server: choosing it then is
/// refused out loud, with the sentence of the command that is running, which says more than a greyed menu item.
/// These records describe the commands; the pages that carry them out are built on them.
/// </para>
/// </summary>
public sealed record ServerCommands(IReadOnlyList<ServerCommand> All)
{
   /// <summary>The commands of a server that offers none.</summary>
   public static ServerCommands None { get; } = new([]);



   /// <summary>One command by its kind, or null when the server does not offer it.</summary>
   public ServerCommand? Find(ServerCommandKind kind) => All.FirstOrDefault(command => command.Kind == kind);



   /// <summary>True when the other set lists the same commands in the same states.</summary>
   public bool Same(ServerCommands other)
   {
      ArgumentNullException.ThrowIfNull(other);
      return All.SequenceEqual(other.All);
   }



   /// <summary>
   /// The commands for a server at a place in the list (counted from zero) among so many servers. Reconnect needs a
   /// pairing and a session and is not the operator's to give while the administrator holds the computer
   /// disconnected; sign out needs a session; the first server cannot move up and the last cannot move down.
   /// </summary>
   public static ServerCommands For(ServerSnapshot server, int index, int count)
   {
      ArgumentNullException.ThrowIfNull(server);
      if(server.Removing)
      {
         return None;
      }
      string? reconnect = !server.Paired ? ShellMessages.ReconnectNotPaired
         : !server.Authentication.SignedIn ? ShellMessages.ReconnectNotSignedIn
         : ServerStatus.HeldDisconnected(server) ? ShellMessages.ReconnectHeld
         : null;
      string? up = count == 1 ? ShellMessages.MoveOnlyServer : index == 0 ? ShellMessages.MoveAlreadyFirst : null;
      string? down = count == 1 ? ShellMessages.MoveOnlyServer : index == count - 1 ? ShellMessages.MoveAlreadyLast : null;
      return new ServerCommands(
      [
         Command(ServerCommandKind.Reconnect, "Reconnect", "C", null, reconnect),
         Command(ServerCommandKind.SignOut, "Sign out", "S", null, server.Authentication.SignedIn ? null : ShellMessages.SignOutNotSignedIn),
         Command(ServerCommandKind.Rename, "Rename", "N", "F2", null),
         Command(ServerCommandKind.Remove, "Remove", "V", "Delete", null),
         Command(ServerCommandKind.MoveUp, "Move up", "U", "Alt+Up", up),
         Command(ServerCommandKind.MoveDown, "Move down", "D", "Alt+Down", down),
      ]);
   }



   /// <summary>One command, enabled unless there is a reason why not.</summary>
   private static ServerCommand Command(ServerCommandKind kind, string label, string accessKey, string? gesture, string? disabledReason)
      => new(kind, label, accessKey, gesture, disabledReason is null, disabledReason);
}
