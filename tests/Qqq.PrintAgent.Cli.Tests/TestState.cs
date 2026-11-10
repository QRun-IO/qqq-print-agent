/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// How tests reach the service's state on disk. This is the only place in the test project that constructs the store, so when
/// the store is opened for one server out of a registry, the way a test names "the store in this folder" changes here and
/// nowhere else.
/// </summary>
internal static class TestState
{
   /// <summary>
   /// Opens the store on a state folder, creating the folder and the journal when they are missing, exactly as the service
   /// does. With recovery asked for, a pairing file that cannot be read does not stop the open. The caller disposes the store.
   /// </summary>
   internal static AgentStore OpenStore(string directory, bool recoverUnreadableConfiguration = false) => new(directory, recoverUnreadableConfiguration);



   /// <summary>
   /// The connection string the stores use for the journal in a folder. It names the journal's pool of idle connections, which
   /// is how <see cref="JournalFiles"/> empties that pool and no other before a folder is deleted.
   /// </summary>
   internal static string JournalConnectionString(string directory) => AgentStore.JournalConnectionString(directory);



   /// <summary>The lock every store takes while it opens the journal in a folder, for the tests that hold it to show what an open waits for.</summary>
   internal static Lock SchemaLockFor(string directory) => AgentStore.SchemaLockFor(directory);
}
