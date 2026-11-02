/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Pure-logic checks of how the Windows printer inventory is interpreted: which queues cannot print unattended, how spooler status
/// flags map to the states shown to the operator, and when a failed enumeration must be reported as a failure instead of an empty list.
/// </summary>
public sealed class WindowsPrintersTests
{
   /// <summary>Queues that open a dialog or write to a file (the prompt and FILE: ports, a null port with the PDF driver) are flagged as needing interaction and so are never offered for unattended printing; real USB and network ports are not.</summary>
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



   /// <summary>Spooler flags for a queue that is merely idle or busy doing its job (printing, waiting, processing, warming up, low toner, power save, or combinations of those) all map to READY, so normal activity is never shown as a fault.</summary>
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



   /// <summary>A genuine blocking condition (error, paper problem, no toner, door open, user intervention and similar) is still an ERROR when the printing flag is also set, so a printer that is jammed mid-job is not reported as healthy.</summary>
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



   /// <summary>Offline, paused and unrecognized spooler states keep their own labels instead of collapsing into READY or ERROR, so the operator sees why a queue is unavailable.</summary>
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



   /// <summary>When the enumeration call itself fails (spooler unreachable, access denied, or an unusable buffer response, even if a buffer size was reported), discovery throws with the original Windows error code rather than returning an empty printer list that would look like "no printers".</summary>
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



   /// <summary>The two legitimate outcomes of the first enumeration call are accepted without error: success with nothing to list, and the "insufficient buffer" answer that tells the caller how much space to allocate.</summary>
   [Fact]
   public void DiscoveryProbe_AcceptsEmptySuccessAndExpectedBufferRequest()
   {
      WindowsPrinters.ValidateDiscoveryProbe(true, 0, 0);
      WindowsPrinters.ValidateDiscoveryProbe(false, 122, 512);
   }
}
