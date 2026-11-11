/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>The commands of a server's menu, in the order the menu lists them.</summary>
public enum ServerCommandKind
{
   /// <summary>Ask the server's connection to reconnect.</summary>
   Reconnect,

   /// <summary>End the server's session.</summary>
   SignOut,

   /// <summary>Change the server's name.</summary>
   Rename,

   /// <summary>Remove the server from this computer.</summary>
   Remove,

   /// <summary>Move the server one place up in the tree.</summary>
   MoveUp,

   /// <summary>Move the server one place down in the tree.</summary>
   MoveDown,
}
