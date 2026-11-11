/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.ServiceProcess;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The real service control adapter, run without the print service. The service name is a parameter, so these tests
/// read the state of a built-in Windows service that every Windows has and that cannot be stopped (the remote
/// procedure call service), and send the controls only to a name no service has, which Windows refuses without
/// changing anything. Nothing here starts, stops or changes a service, and the print service is never named.
/// </summary>
public sealed class WindowsServiceManagerTests
{
   /// <summary>The remote procedure call service: present and running on every Windows, and it accepts no stop.</summary>
   private const string BuiltIn = "RpcSs";



   /// <summary>The Windows error for a service name that no installed service has.</summary>
   private const int ServiceDoesNotExist = 1060;



   /// <summary>A built-in service is read as running and as not accepting a stop, and reads the same again after the handle forgot what it had read.</summary>
   [Fact]
   public void BuiltInServiceIsReadAsItIs()
   {
      using IManagedService service = new WindowsServiceManager().Open(BuiltIn);
      Assert.Equal(ServiceControllerStatus.Running, service.Status);
      Assert.False(service.CanStop);
      service.Refresh();
      Assert.Equal(ServiceControllerStatus.Running, service.Status);
   }



   /// <summary>
   /// A name no service has is not refused when the service is taken hold of, nor when the handle forgets its reading,
   /// but by every read and every control, each time with an invalid-operation failure that carries Windows' own error
   /// 1060. That is the failure the actions read as "not installed", and nothing was started or stopped to get it.
   /// </summary>
   [Fact]
   public void NameNoServiceHasIsRefusedByEveryReadAndControl()
   {
      using IManagedService service = new WindowsServiceManager().Open("qqq-none-" + Guid.NewGuid().ToString("N")[..12]);
      service.Refresh();
      foreach(Action use in new Action[] { () => _ = service.Status, () => _ = service.CanStop, service.Start, service.Stop })
      {
         InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(use);
         Assert.Equal(ServiceDoesNotExist, Assert.IsType<Win32Exception>(refusal.InnerException).NativeErrorCode);
      }
   }
}
