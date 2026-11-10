/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>The flow's promise to a view: everything it announces is raised on the thread that called it, even though the server, the browser callback and the service all answer on other threads.</summary>
public sealed class SignInFlowThreadingTests
{
   /// <summary>
   /// Runs every operation of the flow on a single-threaded context, like a window's, while each thing it waits for is
   /// completed from a thread-pool thread. Every announcement (stage, offered methods, messages, the certificate exception)
   /// must arrive on the calling thread with that context current. A view updates its controls in these handlers, so one
   /// "do not return to the caller's thread" inside the flow would fail this test instead of crashing the companion.
   /// </summary>
   [Fact]
   public void Announcements_AreRaisedOnTheThreadThatCalledTheFlow()
   {
      using PumpContext pump = new();
      int caller = Environment.CurrentManagedThreadId;
      List<string> offThread = [];
      List<string> announced = [];
      SignInFixture fixture = new()
      {
         ConfigureSession = session =>
         {
            session.Metadata = SignInFixture.Offers(password: true, SignInFixture.Callback);
            session.PendingDiscovery = new TaskCompletionSource<QqqAuthenticationMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.PendingPassword = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
         },
      };
      fixture.Flow.PropertyChanged += (_, change) =>
      {
         announced.Add(change.PropertyName!);
         if(Environment.CurrentManagedThreadId != caller || !ReferenceEquals(SynchronizationContext.Current, pump))
         {
            offThread.Add(change.PropertyName!);
         }
      };

      pump.Run(() => RunEveryOperationAsync(fixture));

      Assert.Empty(offThread);
      Assert.Contains(nameof(SignInFlow.Stage), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(SignInFlow.Feedback), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(SignInFlow.PasswordOffered), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(SignInFlow.BrowserPending), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(SignInFlow.Trust), announced, StringComparer.Ordinal);
      Assert.Contains(nameof(SignInFlow.Readiness), announced, StringComparer.Ordinal);
      Assert.Equal("Sign-in options ready > Signing in > Signed in > Sign-in options ready > Waiting for the browser > Signed in > Account sign-out > Connecting > Device connected > Untrusted certificates refused", fixture.Titles);
   }



   /// <summary>Discovers, signs in by password, discovers again, signs in through the browser, signs out, enrolls and reads and saves the certificate exception, completing every wait from another thread.</summary>
   private static async Task RunEveryOperationAsync(SignInFixture fixture)
   {
      SignInFlow flow = fixture.Flow;
      Task discovery = flow.DiscoverAsync(SignInFixture.Server);
      await Task.Run(() => fixture.Sessions[0].PendingDiscovery!.SetResult(fixture.Sessions[0].Metadata));
      await discovery;

      (Task reached, TaskCompletionSource<string> reply) = fixture.HoldService();
      Task password = flow.SignInWithPasswordAsync("operator", "secret", PairingChoice.KeepCurrent);
      await Task.Run(() => fixture.Sessions[0].PendingPassword!.SetResult());
      await reached;
      await Task.Run(() => reply.SetResult(SignInFixture.SignedInNeedsEnrollment));
      await password;

      discovery = flow.DiscoverAsync(SignInFixture.Server);
      await Task.Run(() => fixture.Sessions[1].PendingDiscovery!.SetResult(fixture.Sessions[1].Metadata));
      await discovery;
      (reached, reply) = fixture.HoldService();
      Task browser = flow.SignInWithBrowserAsync(PairingChoice.KeepCurrent);
      await Task.Run(() => fixture.Inboxes[0].Deliver(fixture.Sessions[1].CallbackForLatestAttempt));
      await reached;
      await Task.Run(() => reply.SetResult(SignInFixture.SignedInAndPrinting));
      await browser;

      await AnswerOffThreadAsync(fixture, () => flow.SignOutAsync(), "Signed out of QQQ.");
      await AnswerOffThreadAsync(fixture, () => flow.EnrollAsync(SignInFixture.Server, "code-123"), "Enrolled device device-42");
      await AnswerOffThreadAsync(fixture, flow.LoadTrustAsync, """{"allowUntrustedCertificate":false}""");
      await AnswerOffThreadAsync(fixture, () => flow.SetTrustAsync(allow: false, null), "Saved.");
   }



   /// <summary>Starts an operation whose only wait is the service, and answers it from a thread-pool thread once the request has arrived.</summary>
   private static async Task AnswerOffThreadAsync(SignInFixture fixture, Func<Task> operation, string answer)
   {
      (Task reached, TaskCompletionSource<string> reply) = fixture.HoldService();
      Task running = operation();
      await reached;
      await Task.Run(() => reply.SetResult(answer));
      await running;
   }
}
