/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The real facts behind the pipe client's trust decision, read without the print service: a pipe this test serves
/// itself, a service name no service has, the status of a built-in Windows service read through a handle that may only
/// read, the test process and a harmless child program. Nothing here connects to the print service's pipe, names the
/// print service or changes anything on this computer.
/// </summary>
public sealed class WindowsPipePeerFactsTests
{
   /// <summary>The remote procedure call service: present and running on every Windows.</summary>
   private const string BuiltIn = "RpcSs";



   /// <summary>How long a child that is expected to finish may take; generous, because the test machine may be busy.</summary>
   private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);



   /// <summary>The fixture program built beside the tests.</summary>
   private static string StandIn => Path.ChangeExtension(typeof(WorkerFixture.Program).Assembly.Location, ".exe");



   /// <summary>The kernel names the process that serves a pipe: for a pipe this test serves, the test process, asked from the client's end.</summary>
   [Fact]
   public async Task ServerOfAPipeIsTheProcessThatServesIt()
   {
      string name = "qqq-peer-facts-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
      await using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      using CancellationTokenSource patience = new(Generous);
      await Task.WhenAll(server.WaitForConnectionAsync(patience.Token), client.ConnectAsync(patience.Token));
      Assert.Equal(checked((uint)Environment.ProcessId), new WindowsPipePeerFacts().ServerProcessId(client.SafePipeHandle));
   }



   /// <summary>A handle that is no pipe cannot say who serves it, and what Windows said about that is passed on.</summary>
   [Fact]
   public void HandleThatIsNoPipeFailsWithWhatWindowsSaid()
   {
      using SafePipeHandle none = new(IntPtr.Zero, ownsHandle: false);
      _ = Assert.Throws<Win32Exception>(() => new WindowsPipePeerFacts().ServerProcessId(none));
   }



   /// <summary>A service name no service has is not refused when the service is taken hold of but when its status is read, with Windows' own error 1060 inside an invalid-operation failure.</summary>
   [Fact]
   public void NameNoServiceHasIsRefusedWhenItsStatusIsRead()
   {
      using IPeerService service = new WindowsPipePeerFacts().OpenService("qqq-none-" + Guid.NewGuid().ToString("N")[..12]);
      InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => service.ReadStatus());
      Assert.Equal(1060, Assert.IsType<Win32Exception>(refusal.InnerException).NativeErrorCode);
   }



   /// <summary>
   /// The status of a service is its state and the process hosting it: for the built-in remote procedure call service,
   /// read here through a handle this test opened for reading only, the state is running (4) and the process id names
   /// a running process. A handle that is no service handle fails with what Windows said.
   /// </summary>
   [Fact]
   public void StatusOfABuiltInServiceIsItsStateAndTheProcessHostingIt()
   {
      using ReadOnlyServiceHandle manager = OpenSCManagerW(null, null, 0x0001);
      Assert.False(manager.IsInvalid, "The service control manager could not be opened for connecting, which any user may do.");
      using ReadOnlyServiceHandle service = OpenServiceW(manager, BuiltIn, 0x0004);
      Assert.False(service.IsInvalid, "The built-in service could not be opened for reading its status, which any user may do.");
      PeerServiceStatus status = WindowsPeerService.Query(service);
      Assert.Equal(4u, status.State);
      Assert.NotEqual(0u, status.ProcessId);
      using var host = Process.GetProcessById(checked((int)status.ProcessId));
      Assert.Equal(checked((int)status.ProcessId), host.Id);
      using ReadOnlyServiceHandle none = new();
      Assert.Equal(6, Assert.Throws<Win32Exception>(() => WindowsPeerService.Query(none)).NativeErrorCode);
   }



   /// <summary>
   /// The test process is looked at as it is: it has not ended, it runs the program this process runs, and whether it
   /// runs as SYSTEM agrees with the account of the process read another way.
   /// </summary>
   [Fact]
   public void RunningProcessIsLookedAtAsItIs()
   {
      using IPeerProcess me = new WindowsPipePeerFacts().OpenProcess(checked((uint)Environment.ProcessId));
      using var identity = WindowsIdentity.GetCurrent();
      Assert.Equal(identity.IsSystem, me.RunsAsSystem());
      Assert.False(me.HasExited);
      Assert.Equal(Environment.ProcessPath, me.ProgramPath, ignoreCase: true);
   }



   /// <summary>
   /// An id no running process has is refused with an argument failure, which is the sign the trust decision takes for
   /// "the service went away", and an id a process cannot have with an overflow, which it does not.
   /// </summary>
   [Fact]
   public async Task IdNoRunningProcessHasIsRefusedWithAnArgumentFailure()
   {
      using Process ended = Process.Start(new ProcessStartInfo(StandIn, ["--stand-in", "exit", "0", "", "0"]) { UseShellExecute = false, CreateNoWindow = true })!;
      using CancellationTokenSource patience = new(Generous);
      await ended.WaitForExitAsync(patience.Token);
      WindowsPipePeerFacts facts = new();
      _ = Assert.Throws<ArgumentException>(() => facts.OpenProcess(checked((uint)ended.Id)));
      _ = Assert.Throws<OverflowException>(() => facts.OpenProcess(uint.MaxValue));
   }



   /// <summary>
   /// A process that was opened and has ended since reports its end through the handle that is still held. The
   /// process is a harmless child program that this test ends itself.
   /// </summary>
   [Fact]
   public async Task ProcessThatEndsWhileHeldReportsItsEnd()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-peer-facts-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         using Process child = Process.Start(new ProcessStartInfo(StandIn, ["--stand-in", "wait", Path.Combine(root, "started.txt")]) { UseShellExecute = false, CreateNoWindow = true })!;
         try
         {
            using IPeerProcess held = new WindowsPipePeerFacts().OpenProcess(checked((uint)child.Id));
            using var identity = WindowsIdentity.GetCurrent();
            Assert.Equal(identity.IsSystem, held.RunsAsSystem());
            Assert.False(held.HasExited);
            child.Kill();
            using CancellationTokenSource patience = new(Generous);
            await child.WaitForExitAsync(patience.Token);
            Assert.True(held.HasExited);
         }
         finally
         {
            if(!child.HasExited)
            { child.Kill(); }
         }
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>A handle to the service control manager or to a service, opened by this test with read-only access and closed when disposed.</summary>
   private sealed class ReadOnlyServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
   {
      /// <summary>An empty handle, which the marshaller fills, or which stands for "no service" when it is not filled.</summary>
      public ReadOnlyServiceHandle() : base(ownsHandle: true)
      {
      }



      /// <summary>Closes the handle.</summary>
      protected override bool ReleaseHandle() => CloseServiceHandle(handle);
   }



   /// <summary>Opens the service control manager of this computer with the access asked for; 1 is the right to connect.</summary>
   [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
   private static extern ReadOnlyServiceHandle OpenSCManagerW(string? machine, string? database, uint access);



   /// <summary>Opens a service with the access asked for; 4 is the right to read its status and nothing more.</summary>
   [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
   private static extern ReadOnlyServiceHandle OpenServiceW(ReadOnlyServiceHandle manager, string name, uint access);



   /// <summary>Closes a handle to the service control manager or to a service.</summary>
   [DllImport("advapi32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool CloseServiceHandle(IntPtr handle);
}
