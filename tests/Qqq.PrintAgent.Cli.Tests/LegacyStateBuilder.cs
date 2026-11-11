/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Builds a state folder as an earlier release left it, so that what this build does with such a folder can be tested
/// without installing that release. Each call adds one thing the old release would have written, in the form it wrote it,
/// and nothing here goes through the current store: the journal is made by running a script of the old release's own
/// statements (<c>Fixtures/journal-*.sql</c>), and a protected file is written with the serialization and protection that
/// release used. The calls can be made in any order and repeated. The upgrade from 1.x moves the protected files as well
/// as migrating the journal; the session, a staged session and the set-aside recovery files are added here, each as a
/// method of its own, when the tests of those moves need them.
/// </summary>
internal sealed class LegacyStateBuilder
{
   /// <summary>The journal of a 1.2.1 installation: the full schema of the last release without a schema version, with rows in every table.</summary>
   internal const string Journal121 = "journal-1.2.1.sql";



   /// <summary>The oldest journal history can establish: one six-column events table.</summary>
   internal const string JournalOldest = "journal-oldest.sql";



   /// <summary>The state folder being built, as a full path. The constructor creates it.</summary>
   internal string DirectoryPath { get; }



   /// <summary>Starts a state folder at the given path; the folder is created empty when it does not exist.</summary>
   internal LegacyStateBuilder(string directory)
   {
      DirectoryPath = Path.GetFullPath(directory);
      _ = Directory.CreateDirectory(DirectoryPath);
   }



   /// <summary>
   /// Makes the journal by running one of the fixture scripts against it. The connection is the test project's unpooled
   /// one, so when this returns the journal is closed, its write-ahead log is folded in and removed, and the folder holds
   /// <c>journal.db</c> alone, as after a clean stop.
   /// </summary>
   internal LegacyStateBuilder WithJournal(string script)
   {
      return WithJournalSql(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", script)));
   }



   /// <summary>Runs further SQL against the journal: a row the scripts do not have, or damage a test wants to see handled.</summary>
   internal LegacyStateBuilder WithJournalSql(string sql)
   {
      using SqliteConnection journal = TestState.OpenJournal(DirectoryPath);
      using SqliteCommand command = journal.CreateCommand();
      command.CommandText = sql;
      _ = command.ExecuteNonQuery();
      return this;
   }



   /// <summary>
   /// Writes the pairing as 1.x did (transcribed from the store's <c>SaveConfiguration</c> at tag v1.2.1): the configuration
   /// as JSON with the print API's serializer options, protected with DPAPI for the current account, in
   /// <c>connection.dpapi</c>. The service account's protection is the test account's here.
   /// </summary>
   internal LegacyStateBuilder WithPairing(AgentConfiguration pairing)
   {
      byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(pairing, PrintApiClient.JsonOptions);
      return WithFile("connection.dpapi", ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser));
   }



   /// <summary>Writes a file into the folder byte for byte: a leftover, or a protected file damaged so that it cannot be read.</summary>
   internal LegacyStateBuilder WithFile(string name, byte[] content)
   {
      File.WriteAllBytes(Path.Combine(DirectoryPath, name), content);
      return this;
   }
}
