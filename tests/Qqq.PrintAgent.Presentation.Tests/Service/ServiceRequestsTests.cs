/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>The state-changing requests carry the operation name and exactly the fields the service reads for it.</summary>
public sealed class ServiceRequestsTests
{
   /// <summary>The three job actions name the job; only printing another copy carries the confirmed duplicate-risk flag.</summary>
   [Fact]
   public void JobActions_NameTheJobAndOnlyRetryConfirmsDuplicateRisk()
   {
      Assert.Equal(new PipeRequest("retry-job", JobId: "job-1", ConfirmDuplicateRisk: true), ServiceRequests.RetryJob("job-1"));
      Assert.Equal(new PipeRequest("cancel-job", JobId: "job-1"), ServiceRequests.CancelJob("job-1"));
      Assert.Equal(new PipeRequest("acknowledge-job", JobId: "job-1"), ServiceRequests.AcknowledgeJob("job-1"));
   }



   /// <summary>A test print names the printer, the format and the stock profile.</summary>
   [Fact]
   public void TestPrint_NamesPrinterFormatAndProfile()
      => Assert.Equal(new PipeRequest("test-print", PrinterId: "printer-1", Format: "ZPL", TestProfile: "LABEL_4X6_203"), ServiceRequests.TestPrint("printer-1", "ZPL", "LABEL_4X6_203"));



   /// <summary>Saving details sends the three texts as a device or printer profile, and the printer request also names the printer.</summary>
   [Fact]
   public void Profiles_CarryNameDescriptionAndLocation()
   {
      Assert.Equal(new PipeRequest("set-device-profile", DeviceProfile: new DeviceProfile("Packing station 2", "Outbound labels.", "Dock 4")), ServiceRequests.SetDeviceProfile("Packing station 2", "Outbound labels.", "Dock 4"));
      Assert.Equal(new PipeRequest("set-printer-profile", PrinterId: "printer-1", PrinterProfile: new PrinterProfile("Dock 4 labels", "Zebra by the door.", "Dock 4")),
         ServiceRequests.SetPrinterProfile("printer-1", "Dock 4 labels", "Zebra by the door.", "Dock 4"));
   }



   /// <summary>Log requests carry the settings to save, or nothing at all for clearing.</summary>
   [Fact]
   public void LogRequests_CarryTheSettingsOrNothing()
   {
      LogSettings settings = new(14, 50, 5, DebugLogging: true);

      Assert.Same(settings, ServiceRequests.SetLogSettings(settings).LogSettings);
      Assert.Equal("set-log-settings", ServiceRequests.SetLogSettings(settings).Operation);
      Assert.Equal(new PipeRequest("clear-logs"), ServiceRequests.ClearLogs());
   }
}
