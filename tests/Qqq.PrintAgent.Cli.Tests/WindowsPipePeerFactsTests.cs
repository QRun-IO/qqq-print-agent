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
/// itself under a name of its own, a service name no service has, the status of a built-in Windows service read
/// through a handle that may only read, the test process and a harmless child program. Nothing here connects to the
/// print service's pipe, names the print service or changes anything on this computer.
/// </summary>
public sealed class WindowsPipePeerFactsTests
{
   /// <summary>The remote procedure call service: present and running on every Windows.</summary>
   private const string BuiltIn = "RpcSs";



   /// <summary>The right to ask a process for its exit code and nothing more (PROCESS_QUERY_LIMITED_INFORMATION), which any user has for a process of their own.</summary>
   private const uint QueryLimitedInformation = 0x1000;



   /// <summary>The exit code Windows reports for a process that has not ended (STILL_ACTIVE).</summary>
   private const uint StillActive = 259;



   /// <summary>The code the harmless child program exits with when it is told to connect to a pipe that is not one of these tests' own.</summary>
   private const int RefusedPipe = 64;



   /// <summary>How long a child that is expected to finish may take; generous, because the test machine may be busy.</summary>
   private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);



   /// <summary>How long handles that Windows and this test let go of are given to be gone, before the test looks at what is left.</summary>
   private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);



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



   /// <summary>
   /// The process named is the one that serves the pipe, not the one that uses it. This test serves a pipe and a
   /// harmless child program connects to it, so the two ends are different processes: asked about that connection,
   /// the answer is the test process and not the child.
   /// </summary>
   [Fact]
   public async Task ServerOfAPipeIsNotTheProcessThatConnectedToIt()
   {
      string name = "qqq-peer-facts-" + Guid.NewGuid().ToString("N");
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
      using Process client = Process.Start(new ProcessStartInfo(StandIn, ["--stand-in", "connect", name]) { UseShellExecute = false, CreateNoWindow = true })!;
      try
      {
         using CancellationTokenSource patience = new(Generous);
         await server.WaitForConnectionAsync(patience.Token);
         Assert.NotEqual(Environment.ProcessId, client.Id);
         Assert.False(client.HasExited);
         Assert.Equal(checked((uint)Environment.ProcessId), new WindowsPipePeerFacts().ServerProcessId(server.SafePipeHandle));
      }
      finally
      {
         client.Kill();
      }
   }



   /// <summary>
   /// The harmless child program connects only to a pipe of these tests, whose name begins as theirs do and goes on
   /// with letters and digits. Told any other name it connects to nothing, says why and ends with its refusal code:
   /// a name that begins otherwise, and a name that begins as the tests' do and then leaves that beginning by a
   /// separator and two dots, which Windows reads as the name behind them. No pipe has any of these names, and none
   /// is the print service's; a child that did not refuse would wait a minute for the pipe and fail this test.
   /// </summary>
   [Theory]
   [InlineData("qqq-other-facts-")]
   [InlineData(@"qqq-peer-facts-x\..\qqq-other-facts-")]
   [InlineData("qqq-peer-facts-x/../qqq-other-facts-")]
   public async Task StandInRefusesToConnectToAPipeThatIsNotOneOfTheTests(string beginning)
   {
      string name = beginning + Guid.NewGuid().ToString("N");
      using Process client = Process.Start(new ProcessStartInfo(StandIn, ["--stand-in", "connect", name]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
      try
      {
         using CancellationTokenSource patience = new(Generous);
         string said = await client.StandardError.ReadToEndAsync(patience.Token);
         await client.WaitForExitAsync(patience.Token);
         Assert.Equal(RefusedPipe, client.ExitCode);
         Assert.StartsWith("Refused: ", said, StringComparison.Ordinal);
      }
      finally
      {
         client.Kill();
      }
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
   /// A process whose account was asked for through the adapter stays held until the adapter lets it go, which is what
   /// keeps Windows from giving its id to another process during a check. The process is a harmless child program this
   /// test starts and ends itself, and the test keeps no handle of its own to it, so the adapter's is the only one.
   /// Once the child has ended, its id still names it for as long as the adapter holds it, and the adapter reports its
   /// end; once the adapter has let go, the id no longer names it.
   /// </summary>
   [Fact]
   public async Task ProcessThatEndsWhileHeldStaysHeldAndReportsItsEnd()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-peer-facts-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         (int id, DateTime started) = StartChildWithoutKeepingIt(Path.Combine(root, "started.txt"));
         try
         {
            using(IPeerProcess held = new WindowsPipePeerFacts().OpenProcess(checked((uint)id)))
            {
               using var identity = WindowsIdentity.GetCurrent();
               Assert.Equal(identity.IsSystem, held.RunsAsSystem());
               Assert.False(held.HasExited);
               End(id, started);
               await Task.Delay(Settle);
               Assert.True(NamesAnEndedProcess(id), "The adapter did not keep hold of the process: nothing but its handle could keep the id of the ended child from being released.");
               Assert.True(held.HasExited);
            }
            await MsiUpdateSetup.UntilAsync(() => !NamesAnEndedProcess(id));
         }
         finally
         {
            End(id, started);
         }
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>
   /// Starts the stand-in so that it waits until it is ended, and lets go of the handle Windows gave for it at once: a
   /// handle kept here would keep the process, and its id, after it has ended, whatever the adapter under test does.
   /// The child is known afterwards by its id together with the time it started.
   /// </summary>
   private static (int Id, DateTime Started) StartChildWithoutKeepingIt(string announced)
   {
      using Process child = Process.Start(new ProcessStartInfo(StandIn, ["--stand-in", "wait", announced]) { UseShellExecute = false, CreateNoWindow = true })!;
      return (child.Id, child.StartTime);
   }



   /// <summary>
   /// Ends the child this test started and waits until it has ended, through a handle that is let go again. A process
   /// that has the child's id and did not start when the child did is another process and is left alone, as is an id
   /// no running process has.
   /// </summary>
   private static void End(int id, DateTime started)
   {
      try
      {
         using var child = Process.GetProcessById(id);
         if(child.StartTime == started)
         {
            child.Kill();
            Assert.True(child.WaitForExit(Generous), "The child program did not end within a minute of being ended; the machine was too busy for this test.");
         }
      }
      catch(Exception exception) when(exception is ArgumentException or InvalidOperationException or Win32Exception)
      {
         // No running process has the id, or the one that has it now cannot be looked at and so is not the child.
      }
   }



   /// <summary>
   /// Whether the id still names a process that has ended: Windows can open it, and it has an exit code. An id that
   /// names nothing does not, and neither does one that Windows has meanwhile given to a process that is running.
   /// </summary>
   private static bool NamesAnEndedProcess(int id)
   {
      using SafeProcessHandle process = OpenProcess(QueryLimitedInformation, inherit: false, id);
      return !process.IsInvalid && GetExitCodeProcess(process, out uint code) && code != StillActive;
   }



   /// <summary>Opens a process by its id with the access asked for; the handle is invalid when no process has the id.</summary>
   [DllImport("kernel32.dll", SetLastError = true)]
   private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);



   /// <summary>Reads the exit code of a process; 259 while it has not ended.</summary>
   [DllImport("kernel32.dll", SetLastError = true)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);



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
