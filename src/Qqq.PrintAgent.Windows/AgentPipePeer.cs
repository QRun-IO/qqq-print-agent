/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Authenticates the connected kernel pipe instance before any request bytes are sent. The decision is made here; the
/// facts it is made from (which process serves the pipe, what the service control manager says, what the process is)
/// come through <see cref="IPipePeerFacts"/>, so each way the other end can fail to be the service is run against a fake.
/// </summary>
internal sealed class AgentPipePeer : IDisposable
{
   /// <summary>The name the MSI registers the print service under.</summary>
   private const string ServiceName = "QqqPrintAgent";



   /// <summary>The state Windows reports for a service that is running.</summary>
   private const uint ServiceRunning = 4;



   /// <summary>
   /// The check of the installed product: the real Windows facts and the service program at its fixed machine-wide
   /// install path. The two entry points for the installed product go through it and pass nothing of their own, so what
   /// the installed product trusts is this one value, which a test reads.
   /// </summary>
   internal static Check Installed { get; } = new(new WindowsPipePeerFacts(), InstalledService);



   /// <summary>The verified service, held as long as the connection that relies on it.</summary>
   private readonly IPeerService _service;



   /// <summary>The verified process, held open so its id cannot be reused while the connection lives.</summary>
   private readonly IPeerProcess _process;



   /// <summary>Holds the verified service and process so they live as long as the connection that relies on them.</summary>
   private AgentPipePeer(IPeerService service, IPeerProcess process)
   {
      _service = service;
      _process = process;
   }



   /// <summary>The service executable at its fixed machine-wide install path; a process running any other program is not the service.</summary>
   internal static string InstalledService => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "Service", "Qqq.PrintAgent.Service.exe");



   /// <summary>Proves that the far end of the pipe is the print service installed on this computer; see the overload for what is checked.</summary>
   internal static AgentPipePeer Authenticate(SafePipeHandle pipe) => Installed.Authenticate(pipe);



   /// <summary>
   /// Proves the process at the far end of the pipe really is the installed print service before the client sends anything.
   /// The Windows service must be running with exactly the pipe server's process id, the process must run as SYSTEM from the
   /// installed service executable, and the id is read again afterwards so a swap during the checks is detected. Holding the
   /// process handle open blocks id reuse. Any doubt throws one exception type and everything acquired is released, so every
   /// caller sees "not verified, nothing sent" the same way; <see cref="Refusal(IPipePeerFacts, uint?, Exception)"/> chooses what the operator is told.
   /// </summary>
   internal static AgentPipePeer Authenticate(SafePipeHandle pipe, IPipePeerFacts facts, string installedService)
   {
      IPeerService service = facts.OpenService(ServiceName);
      IPeerProcess? process = null;
      uint? server = null;
      try
      {
         uint pid = facts.ServerProcessId(pipe);
         server = pid;
         PeerServiceStatus status = service.ReadStatus();
         if(status.State != ServiceRunning || status.ProcessId != pid || pid == 0)
         {
            throw Untrusted();
         }
         process = facts.OpenProcess(pid);
         bool system = process.RunsAsSystem();
         if(!system || process.HasExited || !string.Equals(process.ProgramPath, installedService, StringComparison.OrdinalIgnoreCase)
            || facts.ServerProcessId(pipe) != pid || service.ReadStatus().ProcessId != pid)
         {
            throw Untrusted();
         }
         return new AgentPipePeer(service, process);
      }
      catch(Exception exception)
      {
         process?.Dispose();
         service.Dispose();
         throw Refusal(facts, server, exception);
      }
   }



   /// <summary>What a failed check tells the operator, judged by the real processes of this computer; see the overload.</summary>
   internal static UnauthorizedAccessException Refusal(uint? server, Exception found) => Installed.Refusal(server, found);



   /// <summary>
   /// Chooses what a failed check tells the operator. When the process that was serving the pipe has ended (or the pipe could
   /// no longer say who serves it), the service stopped or restarted between the connection and the check: nothing is wrong
   /// with the installation, so the answer is to try again. When that process is still running and did not pass, it is not the
   /// installed service, and the answer is the one refusal that reveals nothing about which check failed. A check that threw
   /// keeps what it threw as the inner exception, for the log.
   /// </summary>
   internal static UnauthorizedAccessException Refusal(IPipePeerFacts facts, uint? server, Exception found)
   {
      if(server is not { } pid || Ended(facts, pid))
      {
         return Stopped(found);
      }
      return found as UnauthorizedAccessException ?? Untrusted(found);
   }



   /// <summary>Whether no running process has this id any more. A process that cannot be looked at is treated as still running, so doubt never softens the refusal.</summary>
   private static bool Ended(IPipePeerFacts facts, uint pid)
   {
      try
      {
         using IPeerProcess process = facts.OpenProcess(pid);
         return process.HasExited;
      }
      catch(Exception exception) when(exception is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
      {
         ////////////////////////////////////////////////////////////////////////////////
         // the lookup throws an argument exception when no process has the id, which  //
         // is the one certain sign that it ended; every other failure is only doubt   //
         ////////////////////////////////////////////////////////////////////////////////
         return exception is ArgumentException;
      }
   }



   /// <summary>The refusal for an endpoint that was checked and is not the installed service; it says no request was sent and how to repair, and reveals nothing about which check failed.</summary>
   private static UnauthorizedAccessException Untrusted(Exception? cause = null) => new("The local endpoint is not the installed Windows print service. No request was sent. Check Windows Services and repair the machine installation if needed.", cause);



   /// <summary>
   /// The refusal for a service that went away during the check. It is the same exception type as the other refusal, because
   /// either way nothing was sent, but it tells the operator to wait and try again instead of repairing an installation that
   /// is fine.
   /// </summary>
   private static UnauthorizedAccessException Stopped(Exception cause) => new("The Windows print service stopped or restarted before it could be verified. No request was sent. Wait a moment and try again; check Windows Services if this continues.", cause);



   /// <summary>Releases the process handle (which allows the id to be reused) and the service.</summary>
   public void Dispose()
   {
      _process.Dispose();
      _service.Dispose();
   }



   /// <summary>
   /// A check bound to where its facts come from and to the one program it trusts as the service. The installed product
   /// has one, <see cref="Installed"/>; a test makes its own over a fake.
   /// </summary>
   internal sealed class Check(IPipePeerFacts facts, string serviceProgram)
   {
      /// <summary>Where this check's facts come from.</summary>
      internal IPipePeerFacts Facts => facts;



      /// <summary>The one program this check trusts as the service.</summary>
      internal string ServiceProgram => serviceProgram;



      /// <summary>Proves that the far end of the pipe is the service this check is bound to; <see cref="AgentPipePeer.Authenticate(SafePipeHandle, IPipePeerFacts, string)"/> says what is checked.</summary>
      internal AgentPipePeer Authenticate(SafePipeHandle pipe) => AgentPipePeer.Authenticate(pipe, facts, serviceProgram);



      /// <summary>What a failed check tells the operator, judged by this check's facts; <see cref="AgentPipePeer.Refusal(IPipePeerFacts, uint?, Exception)"/> says how.</summary>
      internal UnauthorizedAccessException Refusal(uint? server, Exception found) => AgentPipePeer.Refusal(facts, server, found);
   }
}
