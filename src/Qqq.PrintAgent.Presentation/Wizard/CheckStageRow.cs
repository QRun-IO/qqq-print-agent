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
   /// The most reasons a row shows. Each reason is cut at the contract's limit for a message, but the contract sets
   /// no limit on how many there are, so the row sets one: beyond this many it says how many more there are.
   /// </summary>
   public const int MaximumProblems = 5;



   /// <summary>How bad each outcome is when a stage was reported more than once, the worst first: a failure, then an outcome that is not known, then a stage that was not reached, then a pass.</summary>
   private static readonly CheckStageOutcome[] s_worstFirst = [CheckStageOutcome.Failed, CheckStageOutcome.Unknown, CheckStageOutcome.NotReached, CheckStageOutcome.Passed];



   /// <summary>
   /// The rows for a check's result: always the five stages the contract names, in the contract's order. A stage the
   /// service did not report, or reported with an outcome this version does not know, is shown as unknown and never
   /// as passed; a stage this version does not know is not shown, as the contract asks of a client. A stage the
   /// service reported more than once is shown with the worst of the outcomes it gave, never with the one that
   /// happened to come first, and with the reasons of every entry. The reasons are sentences the service passed on
   /// from the check, which may quote what a server answered, so each is given the contract's printable form before
   /// it is shown; a reason with nothing to show is left out, and beyond <see cref="MaximumProblems"/> the row says
   /// how many more there are instead of showing them.
   /// </summary>
   internal static IReadOnlyList<CheckStageRow> From(ServerCheckResult check)
   {
      List<CheckStageRow> rows = [];
      foreach(string stage in CompatibilityStageResult.Order)
      {
         CompatibilityStageResult[] reported = [.. check.Stages.Where(found => string.Equals(found.Stage, stage, StringComparison.Ordinal))];
         CheckStageOutcome outcome = reported.Length == 0 ? CheckStageOutcome.Unknown : s_worstFirst.First(worst => reported.Any(entry => OutcomeOf(entry) == worst));
         string[] reasons = [.. reported.SelectMany(entry => entry.Problems).Select(problem => ManagementText.Bounded(problem, ManagementProtocol.MaximumMessageCharacters).Trim()).Where(problem => problem.Length > 0)];
         string[] problems = reasons.Length <= MaximumProblems ? reasons : [.. reasons.Take(MaximumProblems), WizardMessages.MoreReasons(reasons.Length - MaximumProblems)];
         string title = WizardMessages.StageTitle(stage);
         string outcomeText = WizardMessages.StageOutcome(outcome);
         rows.Add(new CheckStageRow(stage, title, outcome, outcomeText, problems, WizardMessages.StageName(title, outcomeText, problems)));
      }
      return rows;
   }



   /// <summary>True when the result reports a stage more than once. Such a result repeats itself or contradicts itself, and neither is an answer the wizard takes as passed.</summary>
   internal static bool ReportsAStageTwice(ServerCheckResult check)
      => check.Stages.GroupBy(stage => stage.Stage, StringComparer.Ordinal).Any(entries => entries.Count() > 1);



   /// <summary>How one entry of the result says its stage went; an outcome this version does not know is unknown.</summary>
   private static CheckStageOutcome OutcomeOf(CompatibilityStageResult entry) => entry.Outcome switch
   {
      CompatibilityStageResult.Passed => CheckStageOutcome.Passed,
      CompatibilityStageResult.Failed => CheckStageOutcome.Failed,
      CompatibilityStageResult.NotReached => CheckStageOutcome.NotReached,
      _ => CheckStageOutcome.Unknown,
   };
}
