/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>One Windows service held for the pipe client's trust decision, as long as the connection that relies on it lives.</summary>
internal interface IPeerService : IDisposable
{
   /// <summary>
   /// What the service control manager says now: the service's state and the id of the process hosting it. It fails
   /// with what Windows reported when the service does not exist or the caller may not open it.
   /// </summary>
   PeerServiceStatus ReadStatus();
}
