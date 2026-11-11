/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>
/// One row of the Server step's result: one of the five stages of the compatibility check, what it tests, how it went
/// in words, and the reasons when it failed. The accessible name says all of that in one sentence, so a screen reader
/// reads a row whole and the outcome never depends on a color or a glyph.
/// </summary>
public sealed record CheckStageRow(string Stage, string Title, CheckStageOutcome Outcome, string OutcomeText, IReadOnlyList<string> Problems, string AccessibleName)
{
   /// <summary>
   /// The rows for a check's result: always the five stages the contract names, in the contract's order. A stage the
   /// service did not report, or reported with an outcome this version does not know, is shown as unknown and never
   /// as passed; a stage this version does not know is not shown, as the contract asks of a client. The reasons are
   /// sentences the service passed on from the check, which may quote what a server answered, so each is given the
   /// contract's printable form before it is shown; a reason with nothing to show is left out.
   /// </summary>
   internal static IReadOnlyList<CheckStageRow> From(ServerCheckResult check)
   {
      List<CheckStageRow> rows = [];
      foreach(string stage in CompatibilityStageResult.Order)
      {
         CompatibilityStageResult? reported = check.Stages.FirstOrDefault(found => string.Equals(found.Stage, stage, StringComparison.Ordinal));
         CheckStageOutcome outcome = reported?.Outcome switch
         {
            CompatibilityStageResult.Passed => CheckStageOutcome.Passed,
            CompatibilityStageResult.Failed => CheckStageOutcome.Failed,
            CompatibilityStageResult.NotReached => CheckStageOutcome.NotReached,
            _ => CheckStageOutcome.Unknown,
         };
         string[] problems = [.. (reported?.Problems ?? []).Select(problem => ManagementText.Bounded(problem, ManagementProtocol.MaximumMessageCharacters).Trim()).Where(problem => problem.Length > 0)];
         string title = WizardMessages.StageTitle(stage);
         string outcomeText = WizardMessages.StageOutcome(outcome);
         rows.Add(new CheckStageRow(stage, title, outcome, outcomeText, problems, WizardMessages.StageName(title, outcomeText, problems)));
      }
      return rows;
   }
}
