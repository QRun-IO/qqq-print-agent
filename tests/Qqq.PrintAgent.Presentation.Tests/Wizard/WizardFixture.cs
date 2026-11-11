/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Presentation.Wizard;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Wizard;

/// <summary>
/// A connection wizard on a scripted pipe: the real client, the real gate and a log that keeps values, with everything
/// the wizard announces recorded as it was at the moment it was announced. A test scripts what the service answers,
/// drives the wizard the way a view would, and reads back what was sent, shown, said and logged.
/// </summary>
internal sealed class WizardFixture : IDisposable
{
   /// <summary>The address the tests type, as the contract's golden files write it.</summary>
   internal const string Address = "https://qqq.example.test";

   /// <summary>The sign-in provider the tests' server names.</summary>
   internal const string Provider = "https://sign-in.example.test";

   /// <summary>A server identifier in the contract's one form.</summary>
   internal const string ServerId = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";

   /// <summary>How long a test waits for a check that is expected to end. It is a guard, so a check that never ends fails the test instead of hanging the run; no test asserts it.</summary>
   internal static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);



   /// <summary>Starts a wizard that adds a server, on a computer with no servers and stored data that is ready.</summary>
   internal WizardFixture()
   {
      Client = new ManagementClient(Pipe.SendAsync, Log);
      Assert.True(ConnectionWizard.TryStartAddServer(Snapshot(), Client, Gate, Log, out ConnectionWizard? wizard, out string? refusal, TestData.Time));
      Assert.Null(refusal);
      Wizard = wizard;
      Wizard.PropertyChanged += (_, change) =>
      {
         Changes.Add(change.PropertyName!);
         Threads.Add(Environment.CurrentManagedThreadId);
      };
      Wizard.Result.Raised += (_, said) =>
      {
         Announced.Add(said.Text);
         Moments.Add(new Moment(said.Text, Wizard.ServerState, Wizard.Stages.Count, Wizard.PrimaryAction, Wizard.IsBusy, Gate.IsBusy(OperationGate.Wizard), Wizard.ResultSeverity, Wizard.FocusTarget));
         Threads.Add(Environment.CurrentManagedThreadId);
      };
   }



   /// <summary>The scripted pipe.</summary>
   internal FakePipe Pipe { get; } = new();

   /// <summary>The log, with the values handed to it.</summary>
   internal TemplateLog Log { get; } = new();

   /// <summary>The window's gate.</summary>
   internal OperationGate Gate { get; } = new();

   /// <summary>The real client on the scripted pipe.</summary>
   internal ManagementClient Client { get; }

   /// <summary>The wizard under test.</summary>
   internal ConnectionWizard Wizard { get; }

   /// <summary>Every sentence announced, in order, as the event carried it.</summary>
   internal List<string> Announced { get; } = [];

   /// <summary>What the wizard showed at the moment of each announcement.</summary>
   internal List<Moment> Moments { get; } = [];

   /// <summary>The name of every property change the wizard announced, in order.</summary>
   internal List<string> Changes { get; } = [];

   /// <summary>The thread of every property change and every announcement.</summary>
   internal List<int> Threads { get; } = [];



   /// <summary>A snapshot with this many servers and this stored-data status, made from the contract's golden snapshot.</summary>
   internal static ServiceSnapshot Snapshot(int servers = 0, string status = StoredState.Ready, int removing = 0)
   {
      Assert.True(ManagementJson.TryReadResult(Replies.Golden("snapshot.healthy.reply.json").Reply!, out ServiceSnapshot? golden));
      ServerSnapshot one = golden.Servers[0];
      return golden with
      {
         Computer = golden.Computer with { State = new StoredState(status) },
         Servers = [.. Enumerable.Range(0, servers).Select(index => one with { ServerId = ManagementProtocol.FormatServerId(Guid.NewGuid()), Position = index, Removing = index < removing })],
      };
   }



   /// <summary>
   /// The result of a check. With no failed stage every stage passed; otherwise the stages before the failed one
   /// passed, it failed with the reasons given, and those after it were not reached.
   /// </summary>
   internal static ServerCheckResult Check(int? failedStage = null, string[]? problems = null, string[]? methods = null, string? provider = null, PresentedCertificate? certificate = null,
      string? existing = null)
   {
      List<CompatibilityStageResult> stages = [];
      for(int index = 0; index < CompatibilityStageResult.Order.Count; index++)
      {
         string outcome = failedStage is not { } failed || index < failed ? CompatibilityStageResult.Passed : index == failed ? CompatibilityStageResult.Failed : CompatibilityStageResult.NotReached;
         stages.Add(new CompatibilityStageResult(CompatibilityStageResult.Order[index], outcome, index == failedStage ? problems ?? ["it did not answer"] : []));
      }
      return new ServerCheckResult(stages, methods ?? (failedStage is null ? [ServerCheckResult.Password, ServerCheckResult.Browser] : []), provider, certificate, existing);
   }



   /// <summary>A refused certificate with every part set.</summary>
   internal static PresentedCertificate Certificate(params string[] refusals)
      => new(refusals, Address, new DateTimeOffset(2026, 11, 10, 15, 4, 5, TimeSpan.Zero), "CN=qqq.example.test", "CN=Example Development Root",
         new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), "A1B2C3D4E5F60718293A4B5C6D7E8F9001122334");



   /// <summary>The reply to a check that failed at the secure connection because Windows refused the certificate.</summary>
   internal static ManagementReplyParse CertificateRefused(string? provider = null, params string[] refusals)
      => Replies.Failure(ManagementErrorCode.CertificateUntrusted, Replies.Message, Check(failedStage: 1, problems: ["Windows does not trust the authority that issued the certificate"], provider: provider,
         certificate: Certificate(refusals.Length == 0 ? [PresentedCertificate.UntrustedAuthority] : refusals)));



   /// <summary>A refusal with a code and a message and no details at all, as the wire carries it.</summary>
   internal static ManagementReplyParse Refusal(string code, string message = Replies.Message)
      => Replies.Text("{\"v\":2,\"ok\":false,\"error\":{\"code\":" + System.Text.Json.JsonSerializer.Serialize(code) + ",\"message\":" + System.Text.Json.JsonSerializer.Serialize(message) + "}}");



   /// <summary>Types the address when none is typed yet, scripts the next answer and runs one check to its end.</summary>
   internal async Task CheckAsync(ManagementReplyParse reply)
   {
      if(Wizard.Address.Length == 0)
      {
         Wizard.Address = Address;
      }
      Pipe.Answer(reply);
      await Wizard.CheckAsync();
   }



   /// <summary>The arguments of a check that was sent, by its position among the requests.</summary>
   internal ServersCheckArgs Sent(int index)
   {
      ManagementRequest request = Pipe.Sent[index];
      Assert.Equal(ManagementOperations.ServersCheck, request.Op);
      Assert.Null(request.Server);
      Assert.True(ManagementJson.TryReadArguments(request, out ServersCheckArgs? arguments));
      return arguments;
   }



   /// <summary>Disposes the wizard.</summary>
   public void Dispose() => Wizard.Dispose();



   /// <summary>What the wizard showed when a sentence was announced.</summary>
   internal sealed record Moment(string Text, ServerStepState State, int Rows, WizardAction Primary, bool Busy, bool GateHeld, FeedbackSeverity Severity, FocusTarget Focus);
}
