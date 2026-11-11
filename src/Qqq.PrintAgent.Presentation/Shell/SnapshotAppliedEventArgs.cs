/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// What a page is told when the shell has read a snapshot and everything in the shell follows it. A snapshot says
/// what was true when its request was sent, so what it says about a server whose last change from this window was
/// answered after that may be older than the change and is not shown: <see cref="ShellViewModel.Snapshot"/> then
/// still holds, for that server, the entry from before the change, the same object as before.
/// <see cref="HeldBack"/> names those servers, so a page does not put that older entry over what its own command
/// has just made true; it takes that server's entry from the next snapshot that does not name it.
/// </summary>
public sealed class SnapshotAppliedEventArgs : EventArgs
{
   /// <summary>Records what was held back.</summary>
   internal SnapshotAppliedEventArgs(IReadOnlyList<string> heldBack)
   {
      HeldBack = heldBack;
   }



   /// <summary>
   /// The keys of the gate whose part of this snapshot was not taken: the identifier of every server whose entry is
   /// still the one from before a change, or which is left out because the window had no entry for it yet, and
   /// <see cref="OperationGate.Computer"/> when the computer's part and the order of the servers are. Empty when
   /// everything was taken, and when the read brought no snapshot.
   /// </summary>
   public IReadOnlyList<string> HeldBack { get; }
}
