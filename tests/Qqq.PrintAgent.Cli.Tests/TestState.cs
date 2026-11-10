/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// How tests reach the service's state on disk. This is the only place in the test project that constructs the store, so when
/// the store is opened for one server out of a registry, the way a test names "the store in this folder" changes here and
/// nowhere else. It is also the only place a test gets a connection of its own to a journal, because such a connection has
/// to stay out of the pool the stores of that journal share.
/// </summary>
internal static class TestState
{
   /// <summary>
   /// Opens the store on a state folder, creating the folder and the journal when they are missing, exactly as the service
   /// does. With recovery asked for, a pairing file that cannot be read does not stop the open. The caller disposes the store.
   /// </summary>
   internal static AgentStore OpenStore(string directory, bool recoverUnreadableConfiguration = false) => new(directory, recoverUnreadableConfiguration);



   /// <summary>
   /// Opens a connection of the test's own to the journal in a folder, for seeding rows, damaging the schema or asking SQLite
   /// a question the store has no call for. The connection is not pooled. A pooled one would share the pool of the stores on
   /// that journal without taking the journal's lock as they do, and the SQLite library can then hand one connection to two
   /// openers (see "Testing the service runtime" in docs/DEVELOPMENT.md). Unpooled, it has a native handle no store can be
   /// given, and closing it releases the file at once. The store's type is initialized first because that is what selects
   /// the SQLite library, which a test that opens a journal before any store would otherwise find missing.
   /// </summary>
   internal static SqliteConnection OpenJournal(string directory)
   {
      RuntimeHelpers.RunClassConstructor(typeof(AgentStore).TypeHandle);
      SqliteConnection journal = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "journal.db"), Pooling = false }.ToString());
      journal.Open();
      return journal;
   }



   /// <summary>
   /// The connection string the stores use for the journal in a folder. It names the journal's pool of idle connections, which
   /// is how <see cref="JournalFiles"/> empties that pool and no other before a folder is deleted.
   /// </summary>
   internal static string JournalConnectionString(string directory) => AgentStore.JournalConnectionString(directory);



   /// <summary>The lock every store takes while it opens the journal in a folder, for the tests that hold it to show what an open waits for.</summary>
   internal static Lock SchemaLockFor(string directory) => AgentStore.SchemaLockFor(directory);
}
