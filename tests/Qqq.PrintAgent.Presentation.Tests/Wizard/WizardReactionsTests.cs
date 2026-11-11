/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Reflection;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Wizard;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Wizard;

/// <summary>
/// The table that says what the wizard does with each code of the contract, and the parts of the frame that are
/// values: the modes, the ways a run ends, and the sentences. The codes are always taken from the contract's own
/// list, never from a copy of it, so a code added to the contract fails here until the wizard has a reaction to it.
/// </summary>
public sealed class WizardReactionsTests
{
   /// <summary>What the wizard shows for the codes that mean something particular for a check, or the same for every operation. Every other code of the contract is one a check is not answered with.</summary>
   private static readonly Dictionary<string, (ServerStepState State, string? Sentence, FeedbackSeverity Severity, FocusTarget Focus, bool EndsOnComputerPage)> s_expected = new(StringComparer.Ordinal)
   {
      [ManagementErrorCode.ServerIncompatible] = (ServerStepState.Failed, null, FeedbackSeverity.Error, FocusTarget.PrimaryAction, false),
      [ManagementErrorCode.CertificateUntrusted] = (ServerStepState.Failed, null, FeedbackSeverity.Error, FocusTarget.PrimaryAction, false),
      [ManagementErrorCode.InvalidRequest] = (ServerStepState.Editing, WizardMessages.AddressRefusedByService, FeedbackSeverity.Warning, FocusTarget.StepStart, false),
      [ManagementErrorCode.StateNewer] = (ServerStepState.Editing, WizardMessages.StoredDataNotReady, FeedbackSeverity.Error, FocusTarget.PrimaryAction, true),
      [ManagementErrorCode.MigrationIncomplete] = (ServerStepState.Editing, WizardMessages.StoredDataNotReady, FeedbackSeverity.Error, FocusTarget.PrimaryAction, true),
      [ManagementErrorCode.UpdateInProgress] = (ServerStepState.Editing, WizardMessages.UpdateInProgress, FeedbackSeverity.Warning, FocusTarget.PrimaryAction, false),
      [ManagementErrorCode.Busy] = (ServerStepState.Editing, WizardMessages.Busy, FeedbackSeverity.Warning, FocusTarget.PrimaryAction, false),
   };



   /// <summary>The codes whose refusal says nothing unexpected happened in the log: every one the table has a particular or a common reaction for.</summary>
   private static readonly string[] s_expectedOfACheck =
   [
      ManagementErrorCode.ServerIncompatible, ManagementErrorCode.CertificateUntrusted, ManagementErrorCode.InvalidRequest, ManagementErrorCode.StateNewer, ManagementErrorCode.MigrationIncomplete,
      ManagementErrorCode.UpdateInProgress, ManagementErrorCode.Busy, ManagementErrorCode.InternalError, ManagementErrorCode.UnknownOperation, ManagementErrorCode.UnsupportedVersion,
   ];



   /// <summary>Every code of the contract, from the contract's own list.</summary>
   public static TheoryData<string> ContractCodes() => [.. ManagementErrorCode.All];



   /// <summary>
   /// Every code in the contract's table has an entry in the wizard's table for a check: an entry of its own, never
   /// the fallback. The list walked is the contract's, so a code added there fails this test until the wizard says
   /// what it shows for it. The table covers exactly the operations the wizard sends.
   /// </summary>
   [Fact]
   public void EveryContractCode_HasAReaction_ForServersCheck()
   {
      Assert.Equal([ManagementOperations.ServersCheck], WizardReactions.Operations);
      Assert.True(ManagementErrorCode.All.Count >= 32);
      foreach(string operation in WizardReactions.Operations)
      {
         Assert.Contains(ManagementOperations.All, known => string.Equals(known.Name, operation, StringComparison.Ordinal));
         string[] missing = [.. ManagementErrorCode.All.Where(code => !WizardReactions.TryFind(operation, code, out _))];
         Assert.True(missing.Length == 0, operation + " has no reaction for: " + string.Join(", ", missing));
      }
      FieldInfo[] declared = [.. typeof(ManagementErrorCode).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral)];
      Assert.Equal(ManagementErrorCode.All.Order(StringComparer.Ordinal), declared.Select(field => (string)field.GetRawConstantValue()!).Order(StringComparer.Ordinal));
   }



   /// <summary>The table says which reaction each code gets for a check: the check's own codes, the common ones, and for every other code that a check is not answered with it.</summary>
   [Theory]
   [MemberData(nameof(ContractCodes))]
   public void Table_NamesTheReaction_ForEachCodeOfACheck(string code)
   {
      WizardReaction expected = code switch
      {
         ManagementErrorCode.ServerIncompatible or ManagementErrorCode.CertificateUntrusted => WizardReaction.ShowCheck,
         ManagementErrorCode.InvalidRequest => WizardReaction.ValueNotAccepted,
         ManagementErrorCode.StateNewer or ManagementErrorCode.MigrationIncomplete => WizardReaction.StoredDataNotReady,
         ManagementErrorCode.UpdateInProgress => WizardReaction.UpdateInProgress,
         ManagementErrorCode.Busy => WizardReaction.Busy,
         ManagementErrorCode.InternalError or ManagementErrorCode.UnknownOperation or ManagementErrorCode.UnsupportedVersion => WizardReaction.ShowMessage,
         _ => WizardReaction.NotThisOperations,
      };

      Assert.True(WizardReactions.TryFind(ManagementOperations.ServersCheck, code, out WizardReaction found));
      Assert.Equal(expected, found);
      Assert.Equal(expected, WizardReactions.For(ManagementOperations.ServersCheck, code));
   }



   /// <summary>A code that is not in the table, and a code for an operation the table does not cover, get the fallback: the service's message is shown.</summary>
   [Fact]
   public void CodeWithoutAnEntry_GetsTheFallback()
   {
      Assert.False(WizardReactions.TryFind(ManagementOperations.ServersCheck, "a_code_from_a_later_version", out _));
      Assert.Equal(WizardReaction.ShowMessage, WizardReactions.For(ManagementOperations.ServersCheck, "a_code_from_a_later_version"));
      Assert.False(WizardReactions.TryFind("servers.notOneTheWizardSends", ManagementErrorCode.ServerNotFound, out _));
      Assert.Equal(WizardReaction.ShowMessage, WizardReactions.For("servers.notOneTheWizardSends", ManagementErrorCode.ServerNotFound));
      Assert.True(WizardReactions.TryFind("servers.notOneTheWizardSends", ManagementErrorCode.Busy, out WizardReaction common));
      Assert.Equal(WizardReaction.Busy, common);
   }



   /// <summary>
   /// What the wizard does when a check is refused with each code of the contract, sent as the wire carries it,
   /// without details. The check's own codes and the common ones each have their state, sentence, severity and
   /// focus. Every other code shows the service's message, points at the log and keeps what was typed; one a
   /// check is not expected to be answered with is logged as that. Nothing but the one check is sent, the gate
   /// comes free, and the result is announced once.
   /// </summary>
   [Theory]
   [MemberData(nameof(ContractCodes))]
   public async Task Check_RefusedWithEachContractCode_ShowsItsReaction(string code)
   {
      using WizardFixture fixture = new();
      fixture.Wizard.Address = WizardFixture.Address;
      fixture.Wizard.Name = "Warehouse";

      await fixture.CheckAsync(WizardFixture.Refusal(code, "What the service said"));

      ConnectionWizard wizard = fixture.Wizard;
      bool known = s_expected.TryGetValue(code, out (ServerStepState State, string? Sentence, FeedbackSeverity Severity, FocusTarget Focus, bool EndsOnComputerPage) expected);
      if(!known)
      {
         expected = (ServerStepState.Editing, "The print service could not carry out the check. It said: What the service said. Nothing was stored. Look under Logs for more, then choose Check to try again.", FeedbackSeverity.Error,
            FocusTarget.PrimaryAction, false);
      }
      expected.Sentence ??= "The server did not pass the check, and the print service sent no details. It said: What the service said. Nothing was stored. Choose Check again to repeat the check.";
      Assert.Equal(expected.State, wizard.ServerState);
      Assert.Equal(expected.Sentence, wizard.Result.Text);
      Assert.Equal([WizardMessages.Checking, expected.Sentence], fixture.Announced);
      Assert.Equal(expected.Severity, wizard.ResultSeverity);
      Assert.Equal(expected.Focus, wizard.FocusTarget);
      Assert.Equal(expected.EndsOnComputerPage ? WizardEnd.ComputerPage : null, wizard.End);
      Assert.Empty(wizard.Stages);
      Assert.Null(wizard.PassedCheck);
      Assert.False(wizard.NextAction.Enabled);
      Assert.Equal(WizardFixture.Address, wizard.Address);
      Assert.Equal("Warehouse", wizard.Name);
      Assert.Single(fixture.Pipe.Sent);
      Assert.False(wizard.IsBusy);
      Assert.False(fixture.Gate.IsBusy(OperationGate.Wizard));
      bool unexpected = !s_expectedOfACheck.Contains(code, StringComparer.Ordinal);
      string refused = "WARN The service refused the check of https://qqq.example.test with " + code + (unexpected ? ", which a check is not expected to be answered with." : ".");
      if(expected.State == ServerStepState.Failed)
      {
         Assert.Contains("WARN The check of https://qqq.example.test did not pass (" + code + "), and the service sent no details that could be read.", fixture.Log.Entries, StringComparer.Ordinal);
      }
      else
      {
         Assert.Contains(refused, fixture.Log.Entries, StringComparer.Ordinal);
      }
   }



   /// <summary>When the service says its stored data is not ready, the wizard says so once and ends on the Computer page; after that it does nothing.</summary>
   [Fact]
   public async Task StoredDataNotReady_IsSaid_ThenTheWizardEndsOnTheComputerPage()
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(WizardFixture.Refusal(ManagementErrorCode.MigrationIncomplete));

      Assert.Equal(WizardEnd.ComputerPage, fixture.Wizard.End);
      Assert.True(fixture.Wizard.End!.OpensComputerPage);
      Assert.Null(fixture.Wizard.End.ServerId);
      Assert.Equal("The print service is not running connections because this computer's stored data is not ready, so nothing was checked. The Computer page says why and what to do.", fixture.Wizard.Result.Text);
      Assert.Equal([WizardMessages.Checking, WizardMessages.StoredDataNotReady], fixture.Announced);
      Assert.False(fixture.Wizard.CancelAction.Enabled);
      await fixture.Wizard.CheckAsync();
      Assert.Single(fixture.Pipe.Sent);
   }



   /// <summary>A code the companion's client does not know arrives as an internal error with the service's message, and the wizard shows that message.</summary>
   [Fact]
   public async Task Check_RefusedWithACodeOfALaterVersion_ShowsTheServicesMessage()
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(WizardFixture.Refusal("a_code_from_a_later_version", "Something new happened."));

      Assert.Equal(ServerStepState.Editing, fixture.Wizard.ServerState);
      Assert.Equal("The print service could not carry out the check. It said: Something new happened. Nothing was stored. Look under Logs for more, then choose Check to try again.", fixture.Wizard.Result.Text);
      Assert.Contains("WARN The service refused the check of https://qqq.example.test with internal_error.", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>A refusal whose message says nothing is still said: the step says that the service gave no reason.</summary>
   [Theory]
   [InlineData(ManagementErrorCode.InternalError, "The print service could not carry out the check and gave no reason. Nothing was stored. Look under Logs for more, then choose Check to try again.")]
   [InlineData(ManagementErrorCode.ServerIncompatible, "The server did not pass the check, and the print service sent no details. Nothing was stored. Choose Check again to repeat the check.")]
   public async Task Check_RefusedWithNoMessage_SaysThatNoReasonWasGiven(string code, string sentence)
   {
      using WizardFixture fixture = new();

      await fixture.CheckAsync(WizardFixture.Refusal(code, ""));

      Assert.Equal(sentence, fixture.Wizard.Result.Text);
   }



   /// <summary>A message that already ends a sentence is not given a second full stop.</summary>
   [Theory]
   [InlineData("It stopped.", "It said: It stopped. Nothing")]
   [InlineData("Did it stop?", "It said: Did it stop? Nothing")]
   [InlineData("It stopped!", "It said: It stopped! Nothing")]
   [InlineData("  it stopped  ", "It said: it stopped. Nothing")]
   public void ServiceSaid_EndsTheServicesSentence(string message, string fragment)
   {
      Assert.Contains(fragment, WizardMessages.ServiceSaid(message), StringComparison.Ordinal);
      Assert.Contains(fragment, WizardMessages.FailedWithoutDetails(message), StringComparison.Ordinal);
   }



   /// <summary>A mode is adding a server, which names none, or a repair or a pairing, which names a server by its full identifier and nothing else.</summary>
   [Fact]
   public void Mode_IsAddServer_OrNamesAnExistingServer()
   {
      Assert.Equal(WizardModeKind.AddServer, WizardMode.AddServer.Kind);
      Assert.Null(WizardMode.AddServer.ServerId);
      Assert.Same(WizardMode.AddServer, WizardMode.AddServer);
      var repair = WizardMode.Repair(WizardFixture.ServerId);
      Assert.Equal(WizardModeKind.Repair, repair.Kind);
      Assert.Equal(WizardFixture.ServerId, repair.ServerId);
      var pair = WizardMode.Pair(WizardFixture.ServerId);
      Assert.Equal(WizardModeKind.Pair, pair.Kind);
      Assert.Equal(WizardFixture.ServerId, pair.ServerId);
      Assert.Equal(repair, WizardMode.Repair(WizardFixture.ServerId));
      Assert.NotEqual(repair, pair);
      Assert.NotEqual(WizardMode.AddServer, repair);
      foreach(string notAnIdentifier in new[] { "", "Warehouse", "3F2B8C1E-5D4A-4F6B-9A7C-0E1D2C3B4A5F", "3f2b8c1e", null! })
      {
         Assert.Equal("serverId", Assert.Throws<ArgumentException>(() => WizardMode.Repair(notAnIdentifier)).ParamName);
         Assert.Equal("serverId", Assert.Throws<ArgumentException>(() => WizardMode.Pair(notAnIdentifier)).ParamName);
      }
      Assert.DoesNotContain("Warehouse", Assert.Throws<ArgumentException>(() => WizardMode.Repair("Warehouse")).Message, StringComparison.Ordinal);
   }



   /// <summary>A run ends cancelled, on the Computer page, or on one server named by its full identifier.</summary>
   [Fact]
   public void End_IsCancelled_TheComputerPage_OrAServer()
   {
      Assert.Null(WizardEnd.Cancelled.ServerId);
      Assert.False(WizardEnd.Cancelled.OpensComputerPage);
      Assert.Null(WizardEnd.ComputerPage.ServerId);
      Assert.True(WizardEnd.ComputerPage.OpensComputerPage);
      Assert.NotEqual(WizardEnd.Cancelled, WizardEnd.ComputerPage);
      var server = WizardEnd.Server(WizardFixture.ServerId);
      Assert.Equal(WizardFixture.ServerId, server.ServerId);
      Assert.False(server.OpensComputerPage);
      Assert.Equal(server, WizardEnd.Server(WizardFixture.ServerId));
      Assert.NotEqual(WizardEnd.Cancelled, server);
      Assert.Equal("serverId", Assert.Throws<ArgumentException>(() => WizardEnd.Server("Warehouse")).ParamName);
      Assert.Equal("serverId", Assert.Throws<ArgumentException>(() => WizardEnd.Server(null!)).ParamName);
   }



   /// <summary>Every step, stage, outcome and refusal has words, and a value this version does not know has words that say so.</summary>
   [Fact]
   public void Messages_NameEveryStepStageOutcomeAndRefusal()
   {
      Assert.Equal(["Server", "Sign in", "Pair this computer", "Printers", "Done"], Enum.GetValues<WizardStep>().Select(WizardMessages.StepName), StringComparer.Ordinal);
      Assert.Equal("Step 3 of 4: Printers", WizardMessages.StepTitle(3, 4, WizardStep.Printers));
      Assert.Equal(5, CompatibilityStageResult.Order.Select(WizardMessages.StageTitle).Distinct(StringComparer.Ordinal).Count());
      Assert.DoesNotContain("Another check", CompatibilityStageResult.Order.Select(WizardMessages.StageTitle), StringComparer.Ordinal);
      Assert.Equal("Another check", WizardMessages.StageTitle("licence"));
      Assert.Equal(["Not known", "Passed", "Failed", "Not checked, because an earlier check failed"], Enum.GetValues<CheckStageOutcome>().Select(WizardMessages.StageOutcome), StringComparer.Ordinal);
      Assert.Equal("Reach the address: Passed.", WizardMessages.StageName("Reach the address", "Passed", []));
      Assert.Equal("problems", Assert.Throws<ArgumentNullException>(() => WizardMessages.StageName("a", "b", null!)).ParamName);
      Assert.Equal("origins", Assert.Throws<ArgumentNullException>(() => WizardMessages.PassedUnderException(new SignInOffer(true, false), null!, review: true)).ParamName);
      Assert.Equal(4, new[] { PresentedCertificate.UntrustedAuthority, PresentedCertificate.NameMismatch, PresentedCertificate.NotPresented, "somethingNew" }.Select(WizardMessages.Refusal).Distinct(StringComparer.Ordinal).Count());
      Assert.Null(WizardMessages.Name(ServerNameProblem.None));
      Assert.Equal(6, Enum.GetValues<ServerNameProblem>().Select(WizardMessages.Name).OfType<string>().Distinct(StringComparer.Ordinal).Count());
      Assert.Equal(7, Enum.GetValues<ServerAddressProblem>().Where(problem => problem != ServerAddressProblem.None).Select(WizardMessages.Address).Distinct(StringComparer.Ordinal).Count());
   }



   /// <summary>
   /// The sentences are plain text for an operator: each fixed one is a whole sentence or a button label, none
   /// names a control of the old connection dialog, and none says that a server "is reachable" or "works", which a
   /// check that tests five things and stores nothing does not know.
   /// </summary>
   [Fact]
   public void Messages_AreWholeSentences_AndClaimNothingTheWizardDoesNotKnow()
   {
      string[] labels = [WizardMessages.Check, WizardMessages.CheckAgain, WizardMessages.Next, WizardMessages.Back, WizardMessages.Cancel, WizardMessages.OpenServer, WizardMessages.UseException, WizardMessages.DropException,
         WizardMessages.ExceptionAccept];
      string[] sentences = [.. typeof(WizardMessages).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!)
         .Append(WizardMessages.StartServerLimit).Except(labels, StringComparer.Ordinal)];

      Assert.True(sentences.Length >= 20);
      Assert.All(sentences, sentence =>
      {
         Assert.True(sentence[^1] is '.' or '?', sentence);
         Assert.True(char.IsUpper(sentence[0]), sentence);
         Assert.Equal(sentence.Trim(), sentence);
         Assert.DoesNotContain("  ", sentence, StringComparison.Ordinal);
         foreach(string notKnown in new[] { "reachable", "works", "Discover", "dialog", "pairing choice", "ConnectionSettings", "successfully" })
         {
            Assert.DoesNotContain(notKnown, sentence, StringComparison.OrdinalIgnoreCase);
         }
      });
      Assert.All(labels, label => Assert.False(label.EndsWith('.')));
   }



   /// <summary>The wizard has no member that could hold a secret: no public property of any type of the wizard is a session credential, and none is named for a password, a code or a credential.</summary>
   [Fact]
   public void Wizard_ExposesNothingThatCouldHoldASecret()
   {
      Type[] types = [.. typeof(ConnectionWizard).Assembly.GetTypes().Where(type => string.Equals(type.Namespace, typeof(ConnectionWizard).Namespace, StringComparison.Ordinal))];
      PropertyInfo[] properties = [.. types.SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))];
      FieldInfo[] fields = [.. types.SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))];

      Assert.Contains(typeof(ConnectionWizard), types);
      Assert.True(properties.Length > 30);
      Assert.DoesNotContain(properties, property => property.PropertyType == typeof(QqqSessionCredential) || property.PropertyType == typeof(ManagementPayload));
      Assert.DoesNotContain(fields, field => field.FieldType == typeof(QqqSessionCredential) || field.FieldType == typeof(ManagementPayload));
      foreach(string name in properties.Select(property => property.Name).Concat(fields.Select(field => field.Name)))
      {
         Assert.DoesNotContain("Password", name, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("Credential", name, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("EnrollmentCode", name, StringComparison.OrdinalIgnoreCase);
         Assert.DoesNotContain("Token", name, StringComparison.OrdinalIgnoreCase);
      }
   }
}
