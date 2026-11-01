/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class WindowsPrintersTests
{
   [Theory]
   [InlineData("PORTPROMPT:", "Generic", true)]
   [InlineData("FILE:", "Generic", true)]
   [InlineData("nul:", "Microsoft Print To PDF", true)]
   [InlineData("USB001", "ZDesigner", false)]
   [InlineData("192.0.2.1", "Canon", false)]
   public void InteractiveOutputQueuesCannotPrintUnattended(string port, string driver, bool expected)
   {
      Assert.Equal(expected, WindowsPrinters.RequiresInteractiveOutput(port, driver));
   }

   [Theory]
   [InlineData(0u)]
   [InlineData(0x100u)] // I/O active
   [InlineData(0x200u)] // Busy
   [InlineData(0x400u)] // Printing
   [InlineData(0x2000u)] // Waiting
   [InlineData(0x4000u)] // Processing
   [InlineData(0x8000u)] // Initializing
   [InlineData(0x10000u)] // Warming up
   [InlineData(0x20000u)] // Low toner, still able to print
   [InlineData(0x1000000u)] // Power save
   [InlineData(0x20700u)] // Combined nonblocking states
   public void ActiveOrIdleQueue_IsNotAnError(uint flags)
   {
      Assert.Equal("READY", WindowsPrinters.MapStatus(flags));
   }

   [Theory]
   [InlineData(0x2u)]
   [InlineData(0x4u)]
   [InlineData(0x8u)]
   [InlineData(0x10u)]
   [InlineData(0x20u)]
   [InlineData(0x40u)]
   [InlineData(0x800u)]
   [InlineData(0x1000u)]
   [InlineData(0x40000u)]
   [InlineData(0x80000u)]
   [InlineData(0x100000u)]
   [InlineData(0x200000u)]
   [InlineData(0x400000u)]
   [InlineData(0x4000000u)]
   public void BlockingCondition_RemainsAnErrorWhilePrinting(uint flags)
   {
      Assert.Equal("ERROR", WindowsPrinters.MapStatus(flags | 0x400));
   }

   [Theory]
   [InlineData(0x481u, "OFFLINE")]
   [InlineData(0x2000400u, "OFFLINE")]
   [InlineData(0x401u, "PAUSED")]
   [InlineData(0x800400u, "UNKNOWN")]
   [InlineData(0x80000000u, "UNKNOWN")]
   public void UnavailableAndUnknownStates_ArePreserved(uint flags, string expected)
   {
      Assert.Equal(expected, WindowsPrinters.MapStatus(flags));
   }

   [Theory]
   [InlineData(1722, 0u)] // RPC server unavailable
   [InlineData(5, 0u)] // Access denied
   [InlineData(5, 512u)] // A buffer count must not hide another failure
   [InlineData(122, 0u)] // An unusable insufficient-buffer response
   public void DiscoveryFailure_IsNotReportedAsEmptyInventory(int error, uint needed)
   {
      Win32Exception exception = Assert.Throws<Win32Exception>(() => WindowsPrinters.ValidateDiscoveryProbe(false, error, needed));
      Assert.Equal(error, exception.NativeErrorCode);
   }

   [Fact]
   public void DiscoveryProbe_AcceptsEmptySuccessAndExpectedBufferRequest()
   {
      WindowsPrinters.ValidateDiscoveryProbe(true, 0, 0);
      WindowsPrinters.ValidateDiscoveryProbe(false, 122, 512);
   }
}
