/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The service's runtime as the suite uses it: one computer paired with one server. This is the only type in the test project
/// that names the runtime class, so when the runtime is split into a host with one connection per server, this file changes
/// and the tests do not. Every member is something a test calls today; none was added for later.
/// <para>
/// Members that keep their shape but will mean something narrower once several servers can exist: <see cref="GetSnapshot"/>
/// returns the one combined snapshot of today, which becomes the computer's part joined with the first server's;
/// <see cref="GetTransportSnapshot"/>, <see cref="EnsureAuthorizationAsync"/>, <see cref="SignInAsync"/>,
/// <see cref="SignOutAsync(CancellationToken)"/> and <see cref="EnrollWithGatesAsync"/> act on the runtime as a whole today
/// and will act on the one server's connection; <see cref="PrepareUpdateAsync"/>, <see cref="ResumeAfterUpdate"/> and
/// <see cref="RunAsync"/> stay with the computer. <see cref="PipeAsync(PipeRequest, CancellationToken)"/> speaks the pipe's
/// first protocol, which has no server in it; when the pipe gains its versioned envelope, this is where a request is wrapped.
/// The constants at the top and the pure functions at the bottom are the runtime's today and move to the types that will own them.
/// </para>
/// </summary>
internal sealed class SingleServerHarness : IDisposable
{
   /// <summary>How long a restored or renewed session is used before the service renews it with the server and checks its identity again.</summary>
   internal static TimeSpan SessionResumeInterval => AgentRuntime.SessionResumeInterval;



   /// <summary>How long a reading of the operator's printer catalog stays current before the service reads it again.</summary>
   internal static TimeSpan CatalogRefreshInterval => AgentRuntime.CatalogRefreshInterval;



   /// <summary>How long an update pause lasts when the updater never resumes printing.</summary>
   internal static TimeSpan UpdatePauseDuration => AgentRuntime.UpdatePauseDuration;



   /// <summary>How long update preparation waits for claimed work before it answers that the service is not ready.</summary>
   internal static TimeSpan UpdatePreparationLimit => AgentRuntime.UpdatePreparationLimit;

   private readonly AgentRuntime _runtime;



   /// <summary>The private name of this runtime's management pipe. It is never the installed service's, and it is unique unless the caller passes a name to keep across a restart.</summary>
   internal string PipeName { get; }



   /// <summary>
   /// Builds a runtime on a state folder with the given log, printers, session factory and clock. The harness makes the
   /// management pipe itself, one instance for the current user on a private name, so no test decides how the service listens;
   /// a buffer size is given only by the test that needs a reply to outgrow the pipe's buffer. The last argument runs on the
   /// runtime's command thread just before it saves a command's result, while that thread holds the control state's lock.
   /// </summary>
   internal SingleServerHarness(string directory, Action<string> log, IPrinterBackend printerBackend, Func<Uri, IQqqSessionClient>? sessionFactory = null,
      TimeProvider? timeProvider = null, string? pipeName = null, int pipeBufferSize = 0, Action? controlResultSaving = null)
   {
      string pipe = PipeName = pipeName ?? "qqq-single-server-" + Guid.NewGuid().ToString("N");
      _runtime = new AgentRuntime(directory, log, printerBackend, pipeName: pipe,
         pipeFactory: () => new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, pipeBufferSize, pipeBufferSize),
         sessionFactory: sessionFactory, timeProvider: timeProvider)
      { ControlResultSaving = controlResultSaving };
   }



   /// <summary>Runs the service loop on the state folder until the token is cancelled, as the Windows service does from start to stop.</summary>
   internal Task RunAsync(CancellationToken token) => _runtime.RunAsync(token);



   /// <summary>What the service reports about itself: pairing, sign-in, health, printers, jobs, control state and logs, with no credential in it.</summary>
   internal AgentSnapshot GetSnapshot() => _runtime.GetSnapshot();



   /// <summary>The connection's state and traffic figures alone, read without touching printers or the journal.</summary>
   internal AgentTransportSnapshot GetTransportSnapshot() => _runtime.GetTransportSnapshot();



   /// <summary>Evaluates once whether the service may talk to its server, restoring or renewing the saved session as the reconnect loop and the heartbeat do.</summary>
   internal Task<bool> EnsureAuthorizationAsync(CancellationToken token) => _runtime.EnsureAuthorizationAsync(token);



   /// <summary>Signs the service in with the session a sign-in request carries and returns the reply the pipe would give.</summary>
   internal Task<string> SignInAsync(PipeRequest request, CancellationToken token) => _runtime.SignInAsync(request, token);



   /// <summary>Signs the service out of QQQ only and returns the reply the pipe would give.</summary>
   internal Task<string> SignOutAsync(CancellationToken token) => _runtime.SignOutAsync(token);



   /// <summary>Signs the service out and, when a callback is given, asks for the sign-in provider's own sign-out as well; the reply then carries the provider's request for the companion to open.</summary>
   internal Task<string> SignOutAsync(string? postLogoutRedirectUri, CancellationToken token) => _runtime.SignOutAsync(postLogoutRedirectUri, token);



   /// <summary>Enrolls the computer with the code an enrollment request carries, behind the same gates the pipe command passes, and returns the reply.</summary>
   internal Task<string> EnrollWithGatesAsync(PipeRequest request, CancellationToken token) => _runtime.EnrollWithGatesAsync(request, token);



   /// <summary>Stops new claims and waits for the jobs already claimed, as the update helper asks before it replaces the service; returns the reply.</summary>
   internal Task<string> PrepareUpdateAsync(CancellationToken token) => _runtime.PrepareUpdateAsync(token);



   /// <summary>Lifts the update pause and returns the reply.</summary>
   internal string ResumeAfterUpdate() => _runtime.ResumeAfterUpdate();



   /// <summary>Sends one command over the runtime's management pipe, as the companion and command line do, and returns the reply.</summary>
   internal Task<string> PipeAsync(PipeRequest request, CancellationToken token) => PipeAsync(JsonSerializer.Serialize(request, PrintApiClient.JsonOptions), token);



   /// <summary>Sends one command, given as the text a client would write, over the runtime's management pipe and returns the reply.</summary>
   internal async Task<string> PipeAsync(string command, CancellationToken token)
   {
      await using NamedPipeClientStream pipe = new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
      await pipe.ConnectAsync(token);
      await AgentPipeFraming.WriteAsync(pipe, command, AgentPipeFraming.MaximumCommandCharacters, token);
      return await AgentPipeFraming.ReadAsync(pipe, AgentPipeFraming.MaximumResponseCharacters, token);
   }



   /// <summary>Releases what the runtime holds in memory; the state folder is the caller's to remove.</summary>
   public void Dispose() => _runtime.Dispose();



   /// <summary>The access rules the service puts on its management pipe: LocalSystem and Administrators, and nobody else.</summary>
   internal static PipeSecurity CreatePipeSecurity() => AgentRuntime.CreatePipeSecurity();



   /// <summary>How long the connection loop waits before a given reconnect attempt; the caller supplies the jitter sample, so the schedule can be tested exactly.</summary>
   internal static TimeSpan ReconnectDelay(RealtimeSettings settings, int attempt, double sample) => AgentRuntime.ReconnectDelay(settings, attempt, sample);



   /// <summary>Waits for every task of a finished realtime connection and rethrows one failure as the reason it ended; the other failures go to the callback.</summary>
   internal static Task CompleteConnectionAsync(Action<Exception> alsoFailed, Task reader, params Task[] workers) => AgentRuntime.CompleteConnectionAsync(alsoFailed, reader, workers);



   /// <summary>One page of the job list: the journal's jobs merged, under one sort, with the server's queued jobs that the journal does not hold yet.</summary>
   internal static JobPage QueryJobsWithQueue(AgentStore store, JobQuery query, IReadOnlyList<JobSnapshot> queued) => AgentRuntime.QueryJobsWithQueue(store, query, queued);
}
