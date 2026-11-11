/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>The kinds of page the window's content area can show. The first four belong to one server; the rest belong to the computer.</summary>
public enum PageKind
{
   /// <summary>A server's overview, which is what selecting the server's own node shows.</summary>
   Server,

   /// <summary>The printers this computer shares with one server.</summary>
   Printers,

   /// <summary>The jobs this computer received from one server.</summary>
   Jobs,

   /// <summary>One server's sign-in, pairing and connection.</summary>
   Connection,

   /// <summary>The connection wizard, which is what Add server shows and what a repair or a pairing runs in.</summary>
   Wizard,

   /// <summary>This computer's details, the Windows service and companion startup.</summary>
   Computer,

   /// <summary>The activity log.</summary>
   Logs,

   /// <summary>Appearance, updates and application information.</summary>
   Settings,
}
