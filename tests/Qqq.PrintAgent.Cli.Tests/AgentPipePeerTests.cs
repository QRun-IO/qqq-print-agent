/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the client tells the operator when the other end of the pipe could not be verified. A service that stopped or
/// restarted during the check is not an installation problem, so it must not be answered with "repair the installation";
/// a process that is still running and is not the service must keep the one refusal.
/// </summary>
public sealed class AgentPipePeerTests
{
   private const string NotTheService = "The local endpoint is not the installed Windows print service. No request was sent. Check Windows Services and repair the machine installation if needed.";
   private const string ServiceStopped = "The Windows print service stopped or restarted before it could be verified. No request was sent. Wait a moment and try again; check Windows Services if this continues.";



   /// <summary>When the process that was serving the pipe has ended, the operator is told the service stopped and to try again, and what stopped the check is kept inside for the log.</summary>
   [Fact]
   public void ServerProcessThatEndedIsReportedAsAStoppedService()
   {
      using Process ended = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
      ended.WaitForExit();
      InvalidOperationException found = new("The process has exited.");

      UnauthorizedAccessException refusal = AgentPipePeer.Refusal(checked((uint)ended.Id), found);

      Assert.Equal(ServiceStopped, refusal.Message);
      Assert.Same(found, refusal.InnerException);
   }



   /// <summary>A failed check against a process that has ended is a stopped service too: there is no longer anything to mistrust.</summary>
   [Fact]
   public void FailedCheckAgainstAnEndedProcessIsReportedAsAStoppedService()
   {
      using Process ended = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
      ended.WaitForExit();

      UnauthorizedAccessException refusal = AgentPipePeer.Refusal(checked((uint)ended.Id), new UnauthorizedAccessException(NotTheService));

      Assert.Equal(ServiceStopped, refusal.Message);
   }



   /// <summary>
   /// A process that is still running and did not pass is not the installed service. A check that failed is handed back as
   /// it is, and a check that threw becomes the same refusal with its cause inside, so the operator never sees which check
   /// it was.
   /// </summary>
   [Fact]
   public void RunningProcessThatDidNotPassKeepsTheRefusal()
   {
      uint running = checked((uint)Environment.ProcessId);
      UnauthorizedAccessException failed = new(NotTheService);
      InvalidOperationException threw = new("Service 'QqqPrintAgent' was not found on computer '.'.");

      UnauthorizedAccessException converted = AgentPipePeer.Refusal(running, threw);

      Assert.Same(failed, AgentPipePeer.Refusal(running, failed));
      Assert.Equal(NotTheService, converted.Message);
      Assert.Same(threw, converted.InnerException);
   }



   /// <summary>
   /// Doubt never softens the refusal. When the process that served the pipe cannot even be looked up (here its id is not
   /// one a process can have), that is not the certain sign that it ended, so the answer stays "not the installed service"
   /// and the operator is not told to simply try again.
   /// </summary>
   [Fact]
   public void ProcessThatCannotBeLookedUpKeepsTheRefusal()
   {
      InvalidOperationException threw = new("The check could not be carried out.");

      UnauthorizedAccessException refusal = AgentPipePeer.Refusal(uint.MaxValue, threw);

      Assert.Equal(NotTheService, refusal.Message);
      Assert.Same(threw, refusal.InnerException);
   }



   /// <summary>A pipe that can no longer say which process serves it has lost its server, so the whole check ends as a stopped service with nothing sent.</summary>
   [Fact]
   public void PipeThatLostItsServerIsReportedAsAStoppedService()
   {
      using SafePipeHandle closed = new(IntPtr.Zero, ownsHandle: false);

      UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() => AgentPipePeer.Authenticate(closed));

      Assert.Equal(ServiceStopped, refusal.Message);
      Assert.NotNull(refusal.InnerException);
   }
}
