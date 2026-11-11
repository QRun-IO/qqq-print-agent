/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Explicit local administrator actions for the fixed installed print service. This class is the entry point for the
/// installed product: it applies the actions in <see cref="AgentServiceActions"/> to the real service control manager,
/// the real processes, the system clock and the installed service's name, and holds no rule of its own.
/// </summary>
public static class AgentServiceControl
{
   /// <summary>The name the MSI registers the print service under.</summary>
   private const string ServiceName = "QqqPrintAgent";



   /// <summary>The actions bound to the real service control manager, the real processes, the system clock and the installed service's name.</summary>
   internal static AgentServiceActions Installed { get; } = new(new WindowsServiceManager(), new WindowsProcessHost(), TimeProvider.System, ServiceName);



   /// <summary>Read service state even when the print management pipe is unavailable.</summary>
   [ExcludeFromCodeCoverage(Justification = "Reads the state of the print service installed on this computer, which differs from one computer to the next and which a test must not depend on. It only passes the call on; how a state reads is tested in AgentServiceActions with a fake of IServiceManager, the real adapter in WindowsServiceManagerTests against a built-in service, and the service name bound here in AgentServiceActionsTests.")]
   public static AgentServiceStatus GetStatus() => Installed.GetStatus();



   /// <summary>Start an installed stopped service and wait at most 30 seconds.</summary>
   [ExcludeFromCodeCoverage(Justification = "Starts the print service installed on this computer, which a test must never do. It only passes the call on; when the service is started and how long it is waited for is tested in AgentServiceActions with a fake of IServiceManager and a manual clock, and the service name bound here in AgentServiceActionsTests.")]
   public static Task<AgentServiceStatus> StartAsync() => Installed.StartAsync();



   /// <summary>Restart only after an explicit administrator action; callers must warn about active submissions.</summary>
   [ExcludeFromCodeCoverage(Justification = "Stops and starts the print service installed on this computer, which a test must never do. It only passes the call on; the order of the stop and the start and how long each is waited for is tested in AgentServiceActions with a fake of IServiceManager and a manual clock, and the service name bound here in AgentServiceActionsTests.")]
   public static Task<AgentServiceStatus> RestartAsync() => Installed.RestartAsync();



   /// <summary>Open the native Services console on explicit user request.</summary>
   [ExcludeFromCodeCoverage(Justification = "Opens the Services console on the desktop of this computer, which asks for elevation and stays open; a test must not. It only passes the call on; which program is started with which argument is tested in AgentServiceActions with a fake of IProcessHost, and starting a program in WindowsProcessHostTests with a harmless child program.")]
   public static void OpenServices() => Installed.OpenServices();
}
