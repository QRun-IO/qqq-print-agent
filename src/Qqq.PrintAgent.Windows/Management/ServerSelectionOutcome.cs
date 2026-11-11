/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>How resolving a <c>--server</c> value against the list of servers ended.</summary>
public enum ServerSelectionOutcome
{
   /// <summary>Exactly one server was selected.</summary>
   Selected,

   /// <summary>This computer has no servers, so nothing can be selected.</summary>
   NoServers,

   /// <summary>No value was given and more than one server exists, so one must be named.</summary>
   SelectorRequired,

   /// <summary>The value is no server's identifier, name or identifier prefix.</summary>
   NotFound,

   /// <summary>The value is the name, or the identifier prefix, of more than one server.</summary>
   Ambiguous,

   /// <summary>The value begins a server's identifier but is shorter than the eight characters a prefix needs.</summary>
   PrefixTooShort,
}
