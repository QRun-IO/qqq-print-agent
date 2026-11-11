/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Whether the companion and the command line tool trust the process at the other end of the management pipe. They
/// send credentials and administrator commands down that pipe, so the tests go through each way the other end can
/// fail to be the installed print service and check that it is refused, that everything acquired for the check is
/// released, and that the operator is told the right one of two things: repair, when something that is not the
/// service answered, or try again, when the service went away during the check. The facts come from a fake; no pipe,
/// service or process of this computer is looked at.
/// </summary>
public sealed class AgentPipePeerTrustTests
{
   /// <summary>The one refusal for an endpoint that is not the installed service.</summary>
   private const string NotTheService = "The local endpoint is not the installed Windows print service. No request was sent. Check Windows Services and repair the machine installation if needed.";



   /// <summary>The refusal for a service that went away during the check.</summary>
   private const string ServiceStopped = "The Windows print service stopped or restarted before it could be verified. No request was sent. Wait a moment and try again; check Windows Services if this continues.";



   /// <summary>
   /// The other end is trusted when the pipe is served by the process that hosts the running print service, that
   /// process runs as SYSTEM from the installed service program, and both the pipe and the service still name that
   /// process when asked a second time. The facts are asked in that order, the service by its fixed name, and the
   /// service and the process stay held until the connection ends, when the process is let go first.
   /// </summary>
   [Fact]
   public void RunningSystemServiceThatServesThePipeIsTrustedAndHeldUntilTheConnectionEnds()
   {
      FakePipePeerFacts facts = new();
      AgentPipePeer peer = Authenticate(facts);
      Assert.Equal(new[] { "OpenService QqqPrintAgent", "ServerProcessId", "ReadStatus", "OpenProcess 5120", "RunsAsSystem", "HasExited", "ProgramPath", "ServerProcessId", "ReadStatus" }, facts.Calls, StringComparer.Ordinal);
      Assert.Equal(2, facts.OpenHandles);
      peer.Dispose();
      Assert.Equal(new[] { "Dispose process", "Dispose service" }, facts.Calls[^2..], StringComparer.Ordinal);
      Assert.Equal(0, facts.OpenHandles);
   }



   /// <summary>Windows reports a program path in whatever case it was started with; the installed service program is recognized either way.</summary>
   [Fact]
   public void InstalledServiceProgramIsRecognizedWhateverTheCaseOfItsPath()
   {
      FakePipePeerFacts facts = new() { ProgramPath = FakePipePeerFacts.ServiceProgram.ToUpperInvariant() };
      using AgentPipePeer peer = Authenticate(facts);
      Assert.Equal(2, facts.OpenHandles);
   }



   /// <summary>
   /// Each way a running process at the other end is not the installed service gets the one refusal that does not say
   /// which check failed, with nothing inside it, and nothing stays held: a service that is not running, a service
   /// hosted by another process than the one serving the pipe, a pipe served by no process, a process that does not
   /// run as SYSTEM, a process running another program or none, and a pipe or a service that names another process
   /// when asked again.
   /// </summary>
   [Theory]
   [InlineData("service-stopped")]
   [InlineData("service-starting")]
   [InlineData("service-stopping")]
   [InlineData("service-paused")]
   [InlineData("service-hosted-elsewhere")]
   [InlineData("pipe-served-by-no-process")]
   [InlineData("not-system")]
   [InlineData("another-installed-program")]
   [InlineData("same-name-elsewhere")]
   [InlineData("no-program")]
   [InlineData("pipe-names-another-process-when-asked-again")]
   [InlineData("service-names-another-process-when-asked-again")]
   public void ProcessThatIsNotTheInstalledServiceIsRefused(string how)
   {
      FakePipePeerFacts facts = new();
      switch(how)
      {
         case "service-stopped":
            facts.Statuses[0] = new PeerServiceStatus(1, FakePipePeerFacts.ServicePid);
            break;
         case "service-starting":
            facts.Statuses[0] = new PeerServiceStatus(2, FakePipePeerFacts.ServicePid);
            break;
         case "service-stopping":
            facts.Statuses[0] = new PeerServiceStatus(3, FakePipePeerFacts.ServicePid);
            break;
         case "service-paused":
            facts.Statuses[0] = new PeerServiceStatus(7, FakePipePeerFacts.ServicePid);
            break;
         case "service-hosted-elsewhere":
            facts.Statuses[0] = new PeerServiceStatus(FakePipePeerFacts.Running, 6000);
            break;
         case "pipe-served-by-no-process":
            facts.ServerPids[0] = 0;
            facts.Statuses[0] = new PeerServiceStatus(FakePipePeerFacts.Running, 0);
            break;
         case "not-system":
            facts.IsSystem = false;
            break;
         case "another-installed-program":
            facts.ProgramPath = @"C:\Program Files\QQQ Print Agent\Cli\qqq-print-agent.exe";
            break;
         case "same-name-elsewhere":
            facts.ProgramPath = @"C:\Users\Public\QQQ Print Agent\Service\Qqq.PrintAgent.Service.exe";
            break;
         case "no-program":
            facts.ProgramPath = null;
            break;
         case "pipe-names-another-process-when-asked-again":
            facts.ServerPids.Add(6000);
            break;
         default:
            facts.Statuses.Add(new PeerServiceStatus(FakePipePeerFacts.Running, 6000));
            break;
      }
      UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() => Authenticate(facts));
      Assert.Equal(NotTheService, refusal.Message);
      Assert.Null(refusal.InnerException);
      Assert.Equal(0, facts.OpenHandles);
      if((how.StartsWith("service-", StringComparison.Ordinal) && !how.EndsWith("again", StringComparison.Ordinal)) || how.StartsWith("pipe-served", StringComparison.Ordinal))
      {
         Assert.DoesNotContain("RunsAsSystem", facts.Calls, StringComparer.Ordinal);
      }
   }



   /// <summary>
   /// A check that could not be carried out is not a pass. While the process that serves the pipe is still running,
   /// or cannot be looked at, the answer stays the one refusal, with what stopped the check kept inside for the log:
   /// a service whose status cannot be read (it is not installed, or the caller may not control it), a process that
   /// cannot be opened, an account or a program that cannot be read, and a pipe that cannot say who serves it when
   /// asked again. A process that cannot be opened is refused the same way whether Windows said so with an error of
   /// its own or .NET with an invalid-operation failure: neither is the sign that the process has ended, so the
   /// failure neither escapes as it is nor turns the answer into "try again".
   /// </summary>
   [Theory]
   [InlineData("service-unreadable")]
   [InlineData("process-unopenable")]
   [InlineData("process-unopenable-as-an-invalid-operation")]
   [InlineData("account-unreadable")]
   [InlineData("program-unreadable")]
   [InlineData("pipe-silent-when-asked-again")]
   public void CheckThatCouldNotBeCarriedOutIsRefusedWithItsCauseInside(string how)
   {
      FakePipePeerFacts facts = new();
      Exception cause = how switch
      {
         "service-unreadable" => new InvalidOperationException("Synthetic: service 'QqqPrintAgent' was not found on computer '.'."),
         "process-unopenable-as-an-invalid-operation" => new InvalidOperationException("Synthetic: the process cannot be looked at."),
         _ => new Win32Exception(5, "Synthetic: access is denied."),
      };
      switch(how)
      {
         case "service-unreadable":
            facts.ServiceFailure = cause;
            break;
         case "process-unopenable":
         case "process-unopenable-as-an-invalid-operation":
            facts.OpenFailure = cause;
            break;
         case "account-unreadable":
            facts.AccountFailure = cause;
            break;
         case "program-unreadable":
            facts.ProgramFailure = cause;
            break;
         default:
            facts.PipeFailure = (2, cause);
            break;
      }
      UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() => Authenticate(facts));
      Assert.Equal(NotTheService, refusal.Message);
      Assert.Same(cause, refusal.InnerException);
      Assert.Equal(0, facts.OpenHandles);
   }



   /// <summary>
   /// When the service went away during the check there is nothing to mistrust, so the operator is told to wait and
   /// try again, not to repair: the pipe can no longer say who serves it, the process that served it is gone or has
   /// ended, or a check failed against a process that has ended since. What was found is kept inside, and nothing
   /// stays held.
   /// </summary>
   [Theory]
   [InlineData("pipe-cannot-say")]
   [InlineData("process-gone")]
   [InlineData("process-ended")]
   [InlineData("not-system-and-ended")]
   public void ServiceThatWentAwayDuringTheCheckIsReportedAsStopped(string how)
   {
      FakePipePeerFacts facts = new();
      switch(how)
      {
         case "pipe-cannot-say":
            facts.PipeFailure = (1, new Win32Exception(233, "Synthetic: no process is on the other end of the pipe."));
            break;
         case "process-gone":
            facts.ProcessRuns = false;
            break;
         case "process-ended":
            facts.HasExited = true;
            break;
         default:
            facts.IsSystem = false;
            facts.HasExited = true;
            break;
      }
      UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() => Authenticate(facts));
      Assert.Equal(ServiceStopped, refusal.Message);
      Assert.NotNull(refusal.InnerException);
      Assert.Equal(0, facts.OpenHandles);
      if(string.Equals(how, "pipe-cannot-say", StringComparison.Ordinal))
      {
         Assert.Same(facts.PipeFailure!.Value.Failure, refusal.InnerException);
         Assert.Equal(new[] { "OpenService QqqPrintAgent", "ServerProcessId", "Dispose service" }, facts.Calls, StringComparer.Ordinal);
      }
   }



   /// <summary>
   /// Everything acquired for the check is released before the refusal is chosen: the process and then the service
   /// are let go, and only then is the process looked up again, through a handle of its own, to see whether it has
   /// ended.
   /// </summary>
   [Fact]
   public void WhatWasAcquiredIsReleasedBeforeTheRefusalIsChosen()
   {
      FakePipePeerFacts facts = new() { IsSystem = false };
      _ = Assert.Throws<UnauthorizedAccessException>(() => Authenticate(facts));
      Assert.Equal(new[] { "OpenService QqqPrintAgent", "ServerProcessId", "ReadStatus", "OpenProcess 5120", "RunsAsSystem", "Dispose process", "Dispose service", "OpenProcess 5120", "HasExited", "Dispose process" }, facts.Calls, StringComparer.Ordinal);
   }



   /// <summary>
   /// The second look at the service asks only which process hosts it. A service that has begun to stop since the
   /// first look and is still hosted by the verified process is trusted as before: that process is held by then, so
   /// its id cannot have passed to another, and a service that has stopped names no process at all.
   /// </summary>
   [Fact]
   public void SecondLookAtTheServiceComparesOnlyTheProcessThatHostsIt()
   {
      FakePipePeerFacts facts = new();
      facts.Statuses.Add(new PeerServiceStatus(3, FakePipePeerFacts.ServicePid));
      using AgentPipePeer peer = Authenticate(facts);
      Assert.Equal(2, facts.Calls.Count(call => string.Equals(call, "ReadStatus", StringComparison.Ordinal)));
      Assert.Equal(2, facts.OpenHandles);
   }



   /// <summary>
   /// The installed product's check is bound to the real Windows facts and trusts only the service program at its fixed
   /// place under this computer's Program Files. Only the binding is read here; Windows is asked nothing.
   /// </summary>
   [Fact]
   public void InstalledProductsCheckIsBoundToWindowsAndTheServiceProgramUnderProgramFiles()
   {
      _ = Assert.IsType<WindowsPipePeerFacts>(AgentPipePeer.Installed.Facts);
      Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "Service", "Qqq.PrintAgent.Service.exe"), AgentPipePeer.Installed.ServiceProgram);
   }



   /// <summary>
   /// A bound check asks its own facts and trusts its own program and no other. Over the same facts, the check bound
   /// to the program the process runs trusts the other end, and the check bound to another program refuses it. A
   /// refusal is judged by the same facts: for an id no process can have, which Windows itself would leave in doubt,
   /// facts that say the process is gone give "try again".
   /// </summary>
   [Fact]
   public void BoundCheckAsksItsOwnFactsAndTrustsItsOwnProgram()
   {
      using SafePipeHandle pipe = new(IntPtr.Zero, ownsHandle: false);
      FakePipePeerFacts facts = new();
      using(new AgentPipePeer.Check(facts, FakePipePeerFacts.ServiceProgram).Authenticate(pipe))
      {
         Assert.Equal(2, facts.OpenHandles);
      }
      UnauthorizedAccessException refusal = Assert.Throws<UnauthorizedAccessException>(() => new AgentPipePeer.Check(facts, @"C:\Program Files\QQQ Print Agent\Cli\qqq-print-agent.exe").Authenticate(pipe));
      Assert.Equal(NotTheService, refusal.Message);
      Assert.Equal(0, facts.OpenHandles);
      facts.ProcessRuns = false;
      Assert.Equal(ServiceStopped, new AgentPipePeer.Check(facts, FakePipePeerFacts.ServiceProgram).Refusal(uint.MaxValue, new InvalidOperationException("Synthetic: a check that threw.")).Message);
      Assert.Equal("OpenProcess 4294967295", facts.Calls[^1]);
   }



   /// <summary>Runs the check against the given facts. The pipe handle is never used by the fake, so a closed one stands in for it.</summary>
   private static AgentPipePeer Authenticate(FakePipePeerFacts facts)
   {
      using SafePipeHandle pipe = new(IntPtr.Zero, ownsHandle: false);
      return AgentPipePeer.Authenticate(pipe, facts, FakePipePeerFacts.ServiceProgram);
   }
}
