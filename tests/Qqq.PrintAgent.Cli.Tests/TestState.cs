/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

// The store's type under the name tests use for it. Every test declares a store as JournalStore, so when the product type
// is renamed, this line and OpenStore below change and no test does.
global using JournalStore = Qqq.PrintAgent.Windows.AgentStore;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// How tests reach the service's state on disk. This is the only place in the test project that constructs the store, so when
/// the store is opened for one server out of a registry, the way a test names "the store in this folder" changes here and
/// nowhere else. Tests name the store's type by the alias declared above, never by the product's name. This is also the
/// only place a test gets a connection of its own to a journal, because such a connection has to stay out of the pool the
/// stores of that journal share, and the only place a test migrates a journal, which the service does when it starts.
/// </summary>
internal static class TestState
{
   /// <summary>
   /// Opens the store on a state folder. The journal is migrated first, as the service does when it starts and before it
   /// opens any store, so a test may name a folder that does not exist yet; a store by itself creates nothing. With
   /// recovery asked for, a pairing file that cannot be read does not stop the open. The caller disposes the store.
   /// </summary>
   internal static AgentStore OpenStore(string directory, bool recoverUnreadableConfiguration = false)
   {
      _ = JournalSchema.Migrate(directory);
      return new(directory, recoverUnreadableConfiguration);
   }



   /// <summary>Opens the store on a state folder without migrating its journal first, for the tests whose subject is what a store does with a journal as it finds it.</summary>
   internal static AgentStore OpenStoreAsFound(string directory) => new(directory);



   /// <summary>Brings the journal in a state folder to the schema version this build works with, as the service does when it starts, and returns what the service would then know about its stored state.</summary>
   internal static StateStatus Migrate(string directory) => JournalSchema.Migrate(directory);



   /// <summary>Migrates with a step list of the test's own: the product's steps with one added, or one wrapped, to show what a failing or a slow step does.</summary>
   internal static StateStatus Migrate(string directory, IReadOnlyList<Action<JournalMigrationContext>> steps) => JournalSchema.Migrate(directory, steps);



   /// <summary>The product's migration steps, oldest first, for a test to build its own list from.</summary>
   internal static IReadOnlyList<Action<JournalMigrationContext>> MigrationSteps => JournalMigration.Steps;



   /// <summary>The schema version this build works with.</summary>
   internal static int SchemaVersion => JournalSchema.Version;



   /// <summary>
   /// Opens a connection of the test's own to the journal in a folder, for seeding rows, damaging the schema or asking SQLite
   /// a question the store has no call for. The connection is not pooled. A pooled one would share the pool of the stores on
   /// that journal without taking the journal's lock as they do, and the SQLite library can then hand one connection to two
   /// openers (see "Testing the service runtime" in docs/DEVELOPMENT.md). Unpooled, it has a native handle no store can be
   /// given, and closing it releases the file at once. It creates the journal file when there is none, which is how a test
   /// builds a journal in the shape an earlier release left.
   /// </summary>
   internal static SqliteConnection OpenJournal(string directory) => JournalDatabase.OpenUnpooled(directory);



   /// <summary>
   /// The connection string the stores use for the journal in a folder. It names the journal's pool of idle connections, which
   /// is how <see cref="JournalFiles"/> empties that pool and no other before a folder is deleted.
   /// </summary>
   internal static string JournalConnectionString(string directory) => JournalDatabase.ConnectionString(directory);



   /// <summary>The lock every store takes while it opens the journal in a folder, and the migration holds while it runs, for the tests that hold it to show what waits for it.</summary>
   internal static Lock SchemaLockFor(string directory) => JournalDatabase.LockFor(directory);
}
