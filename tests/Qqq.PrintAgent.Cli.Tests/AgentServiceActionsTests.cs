/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.ServiceProcess;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the companion's service controls do and say, run against a service that exists only in memory and a clock the
/// test moves: how each state reads and what is offered in it, when the service is started or stopped, in which order,
/// and how long it is waited for. No test here reads, starts or stops a service of this computer.
/// </summary>
public sealed class AgentServiceActionsTests
{
   /// <summary>The name of the service the actions under test are for.</summary>
   private const string Name = "SyntheticPrintService";



   /// <summary>What is said about a service that is not stopped.</summary>
   private const string Independent = "The Windows service runs independently of this companion.";



   /// <summary>What is said about a stopped service.</summary>
   private const string StartIt = "Start the service to connect and print.";



   /// <summary>The pause between two looks at the service while it is waited for.</summary>
   private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(250);



   /// <summary>
   /// The state is shown under Windows' own name for it. Only a stopped service can be started, only a running one
   /// that accepts a stop can be restarted, and a stopped one is the only one the operator is told to start. Whether a
   /// stop is accepted is asked only of a running service, and the service is let go again.
   /// </summary>
   [Theory]
   [InlineData(ServiceControllerStatus.Running, true, false, true, Independent)]
   [InlineData(ServiceControllerStatus.Running, false, false, false, Independent)]
   [InlineData(ServiceControllerStatus.Stopped, true, true, false, StartIt)]
   [InlineData(ServiceControllerStatus.StartPending, true, false, false, Independent)]
   [InlineData(ServiceControllerStatus.StopPending, true, false, false, Independent)]
   [InlineData(ServiceControllerStatus.Paused, true, false, false, Independent)]
   public void StateIsShownWithWhatCanBeDoneInIt(ServiceControllerStatus state, bool acceptsStop, bool canStart, bool canRestart, string guidance)
   {
      var setup = new Setup { Services = { State = state, AcceptsStop = acceptsStop } };
      Assert.Equal(new AgentServiceStatus(true, state.ToString(), canStart, canRestart, guidance), setup.Actions.GetStatus());
      Assert.Equal(state == ServiceControllerStatus.Running ? new[] { "Open " + Name, "Status", "CanStop", "Dispose" } : ["Open " + Name, "Status", "Dispose"], setup.Services.Calls, StringComparer.Ordinal);
   }



   /// <summary>A service name that no installed service has (Windows error 1060) reads as not installed, with where to get the installer.</summary>
   [Fact]
   public void ServiceThatDoesNotExistReadsAsNotInstalled()
   {
      var setup = new Setup { Services = { Refusal = FakeServiceManager.NotInstalled() } };
      Assert.Equal(new AgentServiceStatus(false, "Not installed", false, false, "Install the QQQ Print Agent MSI from the project releases, then reopen this app. Windows Services can confirm the installation."), setup.Actions.GetStatus());
      Assert.Equal(0, setup.Services.OpenHandles);
   }



   /// <summary>Any other refusal from Windows reads as unavailable, never as not installed: access denied, a refusal without an error, and a bare Windows error.</summary>
   [Theory]
   [InlineData("access-denied")]
   [InlineData("no-error")]
   [InlineData("bare-error")]
   public void OtherRefusalsReadAsUnavailable(string refusal)
   {
      var setup = new Setup();
      setup.Services.Refusal = refusal switch
      {
         "access-denied" => FakeServiceManager.Refused(5),
         "no-error" => new InvalidOperationException("Synthetic: the service controller is not usable."),
         _ => new Win32Exception(1060),
      };
      Assert.Equal(new AgentServiceStatus(false, "Unavailable", false, false, "Windows could not read the service state. Use an administrator account and check Windows Services."), setup.Actions.GetStatus());
      Assert.Equal(0, setup.Services.OpenHandles);
   }



   /// <summary>A failure that is not a refusal from Windows is not turned into a state; it reaches the caller, and the service is still let go.</summary>
   [Fact]
   public void FailureThatIsNotARefusalFromWindowsIsNotHidden()
   {
      var setup = new Setup { Services = { Refusal = new IOException("Synthetic: something else failed.") } };
      Assert.Same(setup.Services.Refusal, Assert.Throws<IOException>(setup.Actions.GetStatus));
      Assert.Equal(0, setup.Services.OpenHandles);
   }



   /// <summary>
   /// A stopped service is started once and then looked at every 250 ms, with a fresh reading each time, until it
   /// runs; the state reported at the end is read anew.
   /// </summary>
   [Fact]
   public async Task StoppedServiceIsStartedAndWaitedForUntilItRuns()
   {
      var setup = new Setup();
      setup.Become(ServiceControllerStatus.StartPending, ServiceControllerStatus.StartPending, ServiceControllerStatus.Running);
      Task<AgentServiceStatus> start = setup.Actions.StartAsync();
      await setup.PassPollsAsync(2);
      Assert.Equal(new AgentServiceStatus(true, "Running", false, true, Independent), await start.WaitAsync(MsiUpdateSetup.Patience));
      Assert.Equal(new[] { "Open " + Name, "Status", "Start", "Refresh", "Status", "Refresh", "Status", "Refresh", "Status", "Open " + Name, "Status", "CanStop", "Dispose", "Dispose" }, setup.Services.Calls, StringComparer.Ordinal);
      Assert.Equal(0, setup.Services.OpenHandles);
   }



   /// <summary>A service that already runs, or is already on its way, is not started again; it is only waited for.</summary>
   [Theory]
   [InlineData(ServiceControllerStatus.Running)]
   [InlineData(ServiceControllerStatus.StartPending)]
   public async Task ServiceThatIsNotStoppedIsNotStartedAgain(ServiceControllerStatus state)
   {
      var setup = new Setup { Services = { State = state } };
      setup.Become(ServiceControllerStatus.Running);
      Assert.True((await setup.Actions.StartAsync().WaitAsync(MsiUpdateSetup.Patience)).CanRestart);
      Assert.DoesNotContain("Start", setup.Services.Calls, StringComparer.Ordinal);
      Assert.Equal(0, setup.Clock.PendingTimers);
   }



   /// <summary>
   /// A service that does not come up is waited for for 30 seconds and no longer: after 119 looks a quarter of a second
   /// apart the action is still waiting, and at 30 seconds it fails and says which state was not reached.
   /// </summary>
   [Fact]
   public async Task ServiceThatDoesNotStartWithinThirtySecondsIsReported()
   {
      var setup = new Setup();
      Task<AgentServiceStatus> start = setup.Actions.StartAsync();
      await setup.PassPollsAsync(119);
      await MsiUpdateSetup.UntilAsync(() => setup.Clock.HasTimerDueIn(Poll));
      Assert.False(start.IsCompleted);
      setup.Clock.Advance(Poll);
      System.TimeoutException failure = await Assert.ThrowsAsync<System.TimeoutException>(() => start.WaitAsync(MsiUpdateSetup.Patience));
      Assert.Equal("The service did not reach Running within 30 seconds. Check Windows Services and the activity log.", failure.Message);
      Assert.Equal(121, setup.Services.Calls.Count(call => string.Equals(call, "Refresh", StringComparison.Ordinal)));
      Assert.Equal(0, setup.Services.OpenHandles);
   }



   /// <summary>
   /// The state is looked at before the limit: a service that is found running at the look that falls on the 30th
   /// second, the 121st, is in time, and the action succeeds.
   /// </summary>
   [Fact]
   public async Task ServiceThatRunsAtTheLookOnTheThirtiethSecondIsInTime()
   {
      var setup = new Setup();
      setup.Become([.. Enumerable.Repeat(ServiceControllerStatus.StartPending, 120), ServiceControllerStatus.Running]);
      Task<AgentServiceStatus> start = setup.Actions.StartAsync();
      await setup.PassPollsAsync(120);
      Assert.Equal("Running", (await start.WaitAsync(MsiUpdateSetup.Patience)).State);
      Assert.Equal(121, setup.Services.Calls.Count(call => string.Equals(call, "Refresh", StringComparison.Ordinal)));
      Assert.Equal(0, setup.Clock.PendingTimers);
   }



   /// <summary>A restart stops a service that is not stopped, running or paused alike, waits until it has stopped, and only then starts it and waits until it runs.</summary>
   [Theory]
   [InlineData(ServiceControllerStatus.Running)]
   [InlineData(ServiceControllerStatus.Paused)]
   public async Task ServiceThatIsNotStoppedIsStoppedAndStartedOnlyOnceItHasStopped(ServiceControllerStatus state)
   {
      var setup = new Setup { Services = { State = state } };
      setup.Become(ServiceControllerStatus.StopPending, ServiceControllerStatus.Stopped, ServiceControllerStatus.StartPending, ServiceControllerStatus.Running);
      Task<AgentServiceStatus> restart = setup.Actions.RestartAsync();
      await setup.PassPollsAsync(2);
      Assert.Equal(new AgentServiceStatus(true, "Running", false, true, Independent), await restart.WaitAsync(MsiUpdateSetup.Patience));
      Assert.Equal(new[] { "Open " + Name, "Status", "Stop", "Refresh", "Status", "Refresh", "Status", "Start", "Refresh", "Status", "Refresh", "Status", "Open " + Name, "Status", "CanStop", "Dispose", "Dispose" }, setup.Services.Calls, StringComparer.Ordinal);
   }



   /// <summary>A restart of a service that is already stopped only starts it; nothing is stopped.</summary>
   [Fact]
   public async Task StoppedServiceIsOnlyStartedByARestart()
   {
      var setup = new Setup();
      setup.Become(ServiceControllerStatus.Running);
      Assert.Equal("Running", (await setup.Actions.RestartAsync().WaitAsync(MsiUpdateSetup.Patience)).State);
      Assert.Equal(new[] { "Open " + Name, "Status", "Start", "Refresh", "Status", "Open " + Name, "Status", "CanStop", "Dispose", "Dispose" }, setup.Services.Calls, StringComparer.Ordinal);
   }



   /// <summary>A service that does not stop within 30 seconds is reported as not having stopped, and is not started on top of that.</summary>
   [Fact]
   public async Task ServiceThatDoesNotStopWithinThirtySecondsIsNotStarted()
   {
      var setup = new Setup { Services = { State = ServiceControllerStatus.Running } };
      Task<AgentServiceStatus> restart = setup.Actions.RestartAsync();
      await setup.PassPollsAsync(120);
      System.TimeoutException failure = await Assert.ThrowsAsync<System.TimeoutException>(() => restart.WaitAsync(MsiUpdateSetup.Patience));
      Assert.Equal("The service did not reach Stopped within 30 seconds. Check Windows Services and the activity log.", failure.Message);
      Assert.DoesNotContain("Start", setup.Services.Calls, StringComparer.Ordinal);
      Assert.Equal(0, setup.Services.OpenHandles);
   }



   /// <summary>
   /// What Windows refuses is not dressed up: a service that will not accept a stop, and a service that is not
   /// installed, fail the action with Windows' own refusal, nothing is started, and the service is let go.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task RefusalFromWindowsFailsTheActionAsItIs(bool notInstalled)
   {
      var setup = new Setup { Services = { State = ServiceControllerStatus.Running, AcceptsStop = false } };
      setup.Services.Refusal = notInstalled ? FakeServiceManager.NotInstalled() : null;
      InvalidOperationException restart = await Assert.ThrowsAsync<InvalidOperationException>(setup.Actions.RestartAsync);
      Assert.Equal(notInstalled ? 1060 : 1052, Assert.IsType<Win32Exception>(restart.InnerException).NativeErrorCode);
      if(notInstalled)
      { Assert.Same(setup.Services.Refusal, await Assert.ThrowsAsync<InvalidOperationException>(setup.Actions.StartAsync)); }
      Assert.DoesNotContain("Start", setup.Services.Calls, StringComparer.Ordinal);
      Assert.Equal(0, setup.Services.OpenHandles);
      Assert.Equal(0, setup.Clock.PendingTimers);
   }



   /// <summary>
   /// The Services console is opened by starting the management console of the Windows system folder with the services
   /// snap-in from that same folder as its one argument, through the shell so that Windows can ask for elevation. That
   /// Windows hands back no process for it is not an error.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void ServicesConsoleIsOpenedFromTheSystemFolderThroughTheShell(bool windowsHandsBackAProcess)
   {
      var setup = new Setup();
      if(!windowsHandsBackAProcess)
      { setup.Processes.OnStart = _ => null; }
      setup.Actions.OpenServices();
      System.Diagnostics.ProcessStartInfo start = Assert.Single(setup.Processes.Started);
      Assert.Equal(Path.Combine(Environment.SystemDirectory, "mmc.exe"), start.FileName);
      Assert.Equal(new[] { Path.Combine(Environment.SystemDirectory, "services.msc") }, start.ArgumentList, StringComparer.Ordinal);
      Assert.True(start.UseShellExecute);
      Assert.Equal(0, setup.Processes.OpenHandles);
      Assert.Empty(setup.Services.Calls);
   }



   /// <summary>The installed product's controls are for the service the MSI registers, by its fixed name. Only the name is read; the service is not.</summary>
   [Fact]
   public void InstalledProductControlsTheServiceTheMsiRegisters()
   {
      Assert.Equal("QqqPrintAgent", AgentServiceControl.Installed.ServiceName);
   }



   /// <summary>The actions under test over a service, processes and a clock that exist only in memory.</summary>
   private sealed class Setup
   {
      /// <summary>Builds the actions over the fakes.</summary>
      internal Setup()
      {
         Actions = new AgentServiceActions(Services, Processes, Clock, Name);
      }



      /// <summary>The service control manager fake; its one service is stopped unless a test says otherwise.</summary>
      internal FakeServiceManager Services { get; } = new();



      /// <summary>The process fake.</summary>
      internal FakeProcessHost Processes { get; } = new();



      /// <summary>The clock, which moves only when a test advances it.</summary>
      internal ManualTimeProvider Clock { get; } = new();



      /// <summary>The actions under test.</summary>
      internal AgentServiceActions Actions { get; }



      /// <summary>Says which states the service moves through, one each time it is looked at again.</summary>
      internal void Become(params ServiceControllerStatus[] states)
      {
         foreach(ServiceControllerStatus state in states)
         { Services.Next.Enqueue(state); }
      }



      /// <summary>Lets the given number of pauses between two looks pass: each time, waits until the action has begun a pause of exactly 250 ms and then moves the clock by that much.</summary>
      internal async Task PassPollsAsync(int polls)
      {
         for(int poll = 0; poll < polls; poll++)
         {
            await MsiUpdateSetup.UntilAsync(() => Clock.HasTimerDueIn(Poll));
            Clock.Advance(Poll);
         }
      }
   }
}
