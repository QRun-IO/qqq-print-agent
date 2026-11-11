/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The service on state it must not use. An older build can be installed over a newer one between release candidates;
/// it then finds a journal at a version it does not know. It must leave that state
/// exactly as it is, contact no server, and still answer enough over its pipe for an operator to see why and to install
/// the newer build again.
/// </summary>
public sealed class NewerStateTests
{
   /// <summary>The operations the service refuses while a newer build's data is in place: everything that would read or change that data or reach a server, and a name it does not know.</summary>
   private static readonly PipeRequest[] Refused =
   [
      new("printers"),
      new("set-exposure", PrinterId: "printer", Exposed: false),
      new("enroll", ServerUrl: "https://synthetic.invalid", EnrollmentCode: "synthetic-code"),
      new("sign-in"),
      new("sign-out"),
      new("reconnect"),
      new("test-print", PrinterId: "printer", Format: "PDF"),
      new("retry-job", JobId: "job"),
      new("cancel-job", JobId: "job"),
      new("acknowledge-job", JobId: "job"),
      new("job-details", JobId: "job"),
      new("query-jobs", JobQuery: new JobQuery()),
      new("get-device-profile"),
      new("set-device-profile", DeviceProfile: new DeviceProfile("Computer")),
      new("set-printer-profile", PrinterId: "printer", PrinterProfile: new PrinterProfile("Queue")),
      new("get-server-trust"),
      new("set-server-trust", ServerTrust: new ServerTrust()),
      new("no-such-operation"),
   ];



   /// <summary>
   /// A paired, signed-in computer whose journal a newer build has since migrated. The service starts, runs no connection
   /// and serves its pipe. It answers status and the snapshot with the sentence that says a newer build wrote the data and
   /// must be installed again, the transport read, the three log operations and the two update operations; each of those
   /// is asked by name below. Every other operation, each also by name, gets that one sentence as an error. No session is
   /// created, so no server is contacted, and the journal and the protected files have the same bytes at the end.
   /// The reason is in the service's log once, as the error written at the start. A refusal is not an error there: the
   /// entries the snapshot shows have none after all eighteen, and each refusal is in the log file as debug detail,
   /// which is on at that point because the log settings were changed above.
   /// </summary>
   [Fact]
   public async Task NewerState_ServiceAnswersStatusLogsUpdates_NothingElse()
   {
      using SessionFixture fixture = new();
      fixture.SaveSession();
      string directory = fixture.Local.DirectoryPath;
      JournalFiles.Release(directory);
      using(SqliteConnection newerBuild = TestState.OpenJournal(directory))
      {
         using SqliteCommand command = newerBuild.CreateCommand();
         command.CommandText = "PRAGMA user_version=" + (TestState.SchemaVersion + 1).ToString(CultureInfo.InvariantCulture);
         _ = command.ExecuteNonQuery();
      }
      IReadOnlyDictionary<string, string> stateBefore = StoredFiles(directory);
      Assert.Equal(["connection.dpapi", "journal.db", "qqq-session.dpapi"], stateBefore.Keys, StringComparer.Ordinal);
      using SingleServerHarness runtime = fixture.Runtime();
      using CancellationTokenSource stop = new(TimeSpan.FromSeconds(60));
      Task running = runtime.RunAsync(stop.Token);
      try
      {
         await AssertAnsweredAsync(runtime, stop.Token);
         foreach(PipeRequest request in Refused)
         {
            Assert.Equal((request.Operation, "Error: " + StateStatus.NewerBuildText), (request.Operation, await runtime.PipeAsync(request, stop.Token)));
         }
         Assert.DoesNotContain(JsonSerializer.Deserialize<AgentSnapshot>(await runtime.PipeAsync(new PipeRequest("snapshot"), stop.Token), PrintApiClient.JsonOptions)!.Logs, entry => string.Equals(entry.Level, "ERROR", StringComparison.Ordinal));
         string logFiles = string.Concat(Directory.EnumerateFiles(Path.Combine(directory, "logs")).Select(file => System.Text.Encoding.UTF8.GetString(JournalProbe.ReadShared(file))));
         Assert.All(Refused, request => Assert.Contains("Refused '" + request.Operation + "': the stored state cannot be used.", logFiles, StringComparison.Ordinal));
         Assert.Equal("Ready for update.", await runtime.PipeAsync(new PipeRequest("prepare-update"), stop.Token));
         Assert.Equal("Printing resumed.", await runtime.PipeAsync(new PipeRequest("resume-after-update"), stop.Token));
         // Past the pipe, the runtime itself opens no store on this state: the authorization check, which starts by reading the saved session, is refused before it reads anything.
         Assert.Equal(StateStatus.NewerBuildText, (await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.EnsureAuthorizationAsync(stop.Token))).Message);
         Assert.False(running.IsCompleted);
      }
      finally
      {
         await StopAsync(stop, running);
      }
      Assert.Equal(0, fixture.Created);
      fixture.AssertSentNothing(fixture.Session);
      Assert.Contains("No server connection is started", Assert.Single(fixture.Logs, line => line.Contains(StateStatus.NewerBuildText, StringComparison.Ordinal)), StringComparison.Ordinal);
      Assert.Equal(stateBefore, StoredFiles(directory));
   }



   /// <summary>The operations that are answered, asked one by one: status, snapshot, transport and the three log operations.</summary>
   private static async Task AssertAnsweredAsync(SingleServerHarness runtime, CancellationToken token)
   {
      Assert.Equal(StateStatus.NewerBuildText, await runtime.PipeAsync(new PipeRequest("status"), token));
      AgentSnapshot snapshot = JsonSerializer.Deserialize<AgentSnapshot>(await runtime.PipeAsync(new PipeRequest("snapshot"), token), PrintApiClient.JsonOptions)!;
      Assert.Equal((false, false, "RED", StateStatus.NewerBuildText), (snapshot.Enrolled, snapshot.Connected, snapshot.Health, snapshot.Status));
      Assert.Null(snapshot.ServerUrl);
      Assert.Null(snapshot.DeviceId);
      Assert.Empty(snapshot.Printers);
      Assert.Empty(snapshot.Jobs);
      Assert.Equal("DISCONNECTED", snapshot.Transport!.State);
      AgentTransportSnapshot transport = JsonSerializer.Deserialize<AgentTransportSnapshot>(await runtime.PipeAsync(new PipeRequest("transport"), token), PrintApiClient.JsonOptions)!;
      Assert.Equal(("NONE", "DISCONNECTED"), (transport.Protocol, transport.State));
      LogSettings read = JsonSerializer.Deserialize<LogSettings>(await runtime.PipeAsync(new PipeRequest("get-log-settings"), token), PrintApiClient.JsonOptions)!;
      Assert.False(read.DebugLogging);
      LogSettings saved = JsonSerializer.Deserialize<LogSettings>(await runtime.PipeAsync(new PipeRequest("set-log-settings", LogSettings: read with { DebugLogging = true }), token), PrintApiClient.JsonOptions)!;
      Assert.True(saved.DebugLogging);
      Assert.StartsWith("Cleared ", await runtime.PipeAsync(new PipeRequest("clear-logs"), token), StringComparison.Ordinal);
   }



   /// <summary>Stops the service loop as Windows does and waits for it to end. The loop ends either by returning or with the cancellation, and the service host accepts both.</summary>
   private static async Task StopAsync(CancellationTokenSource stop, Task running)
   {
      await stop.CancelAsync();
      try
      {
         await running;
      }
      catch(OperationCanceledException)
      {
         // The loop was waiting for a pipe client when it was stopped.
      }
   }



   /// <summary>The hashes of the files that are the stored state: the journal with its log and index, and the protected files. The activity log and the service's own lock file are not state and are written while the service runs.</summary>
   private static SortedDictionary<string, string> StoredFiles(string directory)
   {
      SortedDictionary<string, string> state = new(StringComparer.OrdinalIgnoreCase);
      foreach(string file in Directory.EnumerateFiles(directory, "journal.db*").Concat(Directory.EnumerateFiles(directory, "*.dpapi")))
      {
         state[Path.GetFileName(file)] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JournalProbe.ReadShared(file)));
      }
      return state;
   }
}
