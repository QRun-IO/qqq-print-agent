/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>
/// The one table that says what the wizard does with each code the service can refuse with, for each operation the
/// wizard sends. It has three parts. The common part holds the codes that mean the same whatever was asked. An
/// operation's own part holds the codes that mean something particular for it. And each operation lists the codes
/// the contract does not have it answer: one of those arriving all the same is shown with the service's message and
/// logged as unexpected, which is how the contract says a client treats a code it can do nothing particular with.
/// <para>
/// Every code of the contract has an entry in one of the three for every operation, and an operation's own entry
/// wins over the common one. A test walks the contract's own list of codes, so a code added to the contract fails
/// the build here until someone has said what the wizard shows for it. A code that has no entry at run time gets
/// <see cref="WizardReaction.ShowMessage"/>.
/// </para>
/// </summary>
internal static class WizardReactions
{
   /// <summary>The codes that mean the same for every operation.</summary>
   private static readonly Dictionary<string, WizardReaction> s_common = new(StringComparer.Ordinal)
   {
      [ManagementErrorCode.StateNewer] = WizardReaction.StoredDataNotReady,
      [ManagementErrorCode.MigrationIncomplete] = WizardReaction.StoredDataNotReady,
      [ManagementErrorCode.UpdateInProgress] = WizardReaction.UpdateInProgress,
      [ManagementErrorCode.Busy] = WizardReaction.Busy,
      [ManagementErrorCode.InternalError] = WizardReaction.ShowMessage,
      [ManagementErrorCode.UnknownOperation] = WizardReaction.ShowMessage,
      [ManagementErrorCode.UnsupportedVersion] = WizardReaction.ShowMessage,
   };



   /// <summary>The codes that mean something particular for one operation.</summary>
   private static readonly Dictionary<(string Operation, string Code), WizardReaction> s_own = new()
   {
      [(ManagementOperations.ServersCheck, ManagementErrorCode.ServerIncompatible)] = WizardReaction.ShowCheck,
      [(ManagementOperations.ServersCheck, ManagementErrorCode.CertificateUntrusted)] = WizardReaction.ShowCheck,
      [(ManagementOperations.ServersCheck, ManagementErrorCode.InvalidRequest)] = WizardReaction.ValueNotAccepted,
   };



   /// <summary>
   /// For each operation the wizard sends, the codes the contract does not have it answer. A check is about an address
   /// and no server, session, pairing, printer or job, and it reports a server it could not reach as a failed stage
   /// and an address that is already a server in its result, so none of these is an answer to it.
   /// </summary>
   private static readonly Dictionary<string, string[]> s_notAnswered = new(StringComparer.Ordinal)
   {
      [ManagementOperations.ServersCheck] =
      [
         ManagementErrorCode.ServerNotFound, ManagementErrorCode.ServerAmbiguous, ManagementErrorCode.ServerLimit, ManagementErrorCode.ServerExists,
         ManagementErrorCode.ServerRemoving, ManagementErrorCode.NotSignedIn, ManagementErrorCode.NotPaired, ManagementErrorCode.NotConnected,
         ManagementErrorCode.ProviderChanged, ManagementErrorCode.ServerUnreachable, ManagementErrorCode.ServerRefused, ManagementErrorCode.SignInRefused,
         ManagementErrorCode.BindRefused, ManagementErrorCode.IdentityChanged, ManagementErrorCode.EnrollmentRefused, ManagementErrorCode.EnrollmentUnconfirmed,
         ManagementErrorCode.PrinterNotFound, ManagementErrorCode.JobNotFound, ManagementErrorCode.JobNotCurrent, ManagementErrorCode.DuplicateRiskUnconfirmed,
         ManagementErrorCode.CancelRefused, ManagementErrorCode.StateUnreadable,
      ],
   };



   /// <summary>The operations the wizard sends, which are the operations this table covers.</summary>
   internal static IReadOnlyList<string> Operations { get; } = [.. s_notAnswered.Keys];



   /// <summary>Finds the entry for a code and an operation: the operation's own entry first, then the common one, then the operation's list of codes it does not answer. It is false when the table has no entry, which for a code of the contract is a gap in the table.</summary>
   internal static bool TryFind(string operation, string code, out WizardReaction reaction)
   {
      if(s_own.TryGetValue((operation, code), out reaction) || s_common.TryGetValue(code, out reaction))
      {
         return true;
      }
      reaction = WizardReaction.NotThisOperations;
      return s_notAnswered.TryGetValue(operation, out string[]? codes) && codes.Contains(code, StringComparer.Ordinal);
   }



   /// <summary>What the wizard does with a refusal. A code without an entry is shown with the service's message, as the contract says of a code a client does not know.</summary>
   internal static WizardReaction For(string operation, string code) => TryFind(operation, code, out WizardReaction reaction) ? reaction : WizardReaction.ShowMessage;
}
