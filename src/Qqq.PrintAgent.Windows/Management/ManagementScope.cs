/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>What an operation acts on, which decides whether its request must name a server.</summary>
public enum ManagementScope
{
   /// <summary>The computer or the list of servers. The request has no <c>server</c>.</summary>
   Computer,

   /// <summary>One server. The request must name it by its full identifier.</summary>
   Server,
}
