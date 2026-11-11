/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows service control manager as the service actions need it: a way to take hold of one service by name. An
/// implementation only asks Windows and passes on what it said; what a state means for the operator, when a service is
/// started or stopped and how long it is waited for is decided by <see cref="AgentServiceActions"/>.
/// </summary>
internal interface IServiceManager
{
   /// <summary>
   /// Takes hold of the service with the given name. Nothing is asked of Windows yet, so a name no service has is not
   /// refused here but by the first read or control made through the result.
   /// </summary>
   IManagedService Open(string serviceName);
}
