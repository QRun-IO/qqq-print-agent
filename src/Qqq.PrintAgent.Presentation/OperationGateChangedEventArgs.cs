/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>What <see cref="OperationGate.Changed"/> says: which key became busy or free.</summary>
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



   /// <summary>True when a command has just started under the key; false when it has just ended.</summary>
   public bool Busy { get; }
}
