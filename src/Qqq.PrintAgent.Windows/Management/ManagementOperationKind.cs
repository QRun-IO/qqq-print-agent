/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>How an operation behaves beside others, which is what the pipe's concurrency rules are written in.</summary>
public enum ManagementOperationKind
{
   /// <summary>Changes nothing and never waits for another operation.</summary>
   Read,

   /// <summary>Changes something other than a server's session, pairing or trust.</summary>
   Change,

   /// <summary>Changes one server's session, pairing or trust. These run one at a time per server, and a second one is answered <see cref="ManagementErrorCode.Busy"/> at once instead of waiting.</summary>
   SessionChange,
}
