/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ServiceProcess;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One Windows service held by name: its state as last read, and the two controls the companion offers. The state is
/// read from Windows once and then remembered, so <see cref="Status"/> and <see cref="CanStop"/> describe the same
/// moment until <see cref="Refresh"/> forgets it. Every member that asks Windows fails with
/// <see cref="InvalidOperationException"/> carrying Windows' own error when Windows refuses, for a service that does not
/// exist (error 1060) as for one the caller may not control.
/// </summary>
internal interface IManagedService : IDisposable
{
   /// <summary>The service's state, read from Windows on first use and remembered until <see cref="Refresh"/>.</summary>
   ServiceControllerStatus Status { get; }



   /// <summary>Whether the service accepts a stop request in that same remembered state.</summary>
   bool CanStop { get; }



   /// <summary>Forgets the remembered state, so the next read asks Windows again.</summary>
   void Refresh();



   /// <summary>Asks Windows to start the service and returns without waiting for it to run.</summary>
   void Start();



   /// <summary>Asks Windows to stop the service and returns without waiting for it to stop.</summary>
   void Stop();
}
