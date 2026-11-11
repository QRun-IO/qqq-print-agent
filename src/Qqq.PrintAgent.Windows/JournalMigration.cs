/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The steps that bring a journal's schema from one version to the next, in order. Each step lives in a file of its own
/// (<c>JournalMigration.V1.cs</c> and so on). A step that has shipped is never edited: a journal that has passed it will
/// not run it again, so a change to it would leave old and new installations with different schemas. A change to the
/// schema is a new step at the end of the list.
/// </summary>
internal static partial class JournalMigration
{
   /// <summary>
   /// Every step, oldest first. The step at position n brings a journal from version n to version n + 1, so the number of
   /// steps is the schema version this build works with. <see cref="JournalSchema.Migrate(string)"/> runs the ones a
   /// journal has not passed yet.
   /// </summary>
   internal static IReadOnlyList<Action<JournalMigrationContext>> Steps { get; } = [V1];
}
