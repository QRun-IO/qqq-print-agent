/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ServiceProcess;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One real Windows service behind <see cref="IManagedService"/>. Every member passes the call to the service
/// controller it was given and passes on what comes back, refusals included.
/// </summary>
internal sealed class WindowsManagedService(ServiceController controller) : IManagedService
{
   /// <summary>The state the controller read, or reads now.</summary>
   public ServiceControllerStatus Status => controller.Status;



   /// <summary>Whether the service accepts a stop request, from the same reading.</summary>
   public bool CanStop => controller.CanStop;



   /// <summary>Makes the controller forget what it read.</summary>
   public void Refresh() => controller.Refresh();



   /// <summary>Sends the start request.</summary>
   public void Start() => controller.Start();



   /// <summary>Sends the stop request.</summary>
   public void Stop() => controller.Stop();



   /// <summary>Lets go of the service without changing it.</summary>
   public void Dispose() => controller.Dispose();
}
