/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ServiceProcess;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real service control manager behind <see cref="IServiceManager"/>. It holds a service by name and decides
/// nothing. Nothing here is excluded from coverage: the service name is a parameter, so the tests read a built-in
/// Windows service and send the controls to a name no service has, which changes nothing on the computer.
/// </summary>
internal sealed class WindowsServiceManager : IServiceManager
{
   /// <summary>Holds the named service on this computer; Windows is asked only when the result is used.</summary>
   public IManagedService Open(string serviceName) => new WindowsManagedService(new ServiceController(serviceName));
}
