/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// What <see cref="OperationGate.Changed"/> says: which key became busy or free. It describes the change that raised
/// the event, not the key's state when a listener reads it: the key may have changed again meanwhile, on another
/// thread or from inside an earlier listener. A listener that shows the state reads <see cref="OperationGate.IsBusy"/>.
/// </summary>
public sealed class OperationGateChangedEventArgs : EventArgs
{
   /// <summary>Records the key and its new state.</summary>
   public OperationGateChangedEventArgs(string key, bool busy)
   {
      Key = key;
      Busy = busy;
   }



   /// <summary>The key whose state changed: a server's identifier, <see cref="OperationGate.Computer"/> or <see cref="OperationGate.Wizard"/>.</summary>
   public string Key { get; }



   /// <summary>True when the change that raised this event was a command starting under the key; false when it was one ending. It can be out of date when it is read.</summary>
   public bool Busy { get; }
}
