/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The journal's schema version and the one place it changes. The version is SQLite's <c>PRAGMA user_version</c>: 0 for a
/// journal no versioned build has migrated (a new one, or one a 1.x release left), and from then on the number of
/// migration steps the journal has passed. The service migrates once, when it starts, before anything opens a store.
/// Opening a store only checks the version; it never creates or alters a table.
/// </summary>
internal static class JournalSchema
{
   /// <summary>What a store says when it is opened on a journal the migration has not brought to this build's version.</summary>
   internal const string NotMigratedText = "The print journal has not been brought up to date. The service does that when it starts, and nothing may open the journal before it has.";



   /// <summary>The schema version this build works with: the number of migration steps it knows.</summary>
   internal static int Version => JournalMigration.Steps.Count;



   /// <summary>Brings the journal in a state folder to this build's schema version, creating the folder and the journal when they are missing, and says whether the stored state may be used.</summary>
   internal static StateStatus Migrate(string directory) => Migrate(directory, JournalMigration.Steps);



   /// <summary>
   /// Brings the journal in a state folder to the version of the last of the given steps, under the journal's lock, so
   /// that of several callers one migrates and the others find it done. The version is read first, read-only. A journal
   /// already at the version is ready. One at a higher version was written by a newer build and is left exactly as it is:
   /// it is not opened for writing, and the caller is told both versions. A negative version is one no build writes; such
   /// a journal is refused, because no step list says how to continue from it. Only a journal behind the version is
   /// opened for writing and migrated. The step list is a parameter so that a test can add a step of its own.
   /// </summary>
   internal static StateStatus Migrate(string directory, IReadOnlyList<Action<JournalMigrationContext>> steps)
   {
      string folder = Path.GetFullPath(directory);
      Qqq.PrintAgent.Core.MachineStateDirectory.EnsureForPath(folder);
      _ = Directory.CreateDirectory(folder);
      lock(JournalDatabase.LockFor(folder))
      {
         return Settled(JournalDatabase.ReadVersion(folder), steps.Count) ?? Apply(folder, steps);
      }
   }



   /// <summary>
   /// What a journal at the version found is to a build that knows the given number of steps, when no step has to run:
   /// ready at that number, a newer build's above it, and refused below zero. Null means the journal is behind and has
   /// steps to run. Both readings of the version, the read-only one and the one inside the transaction, are judged here.
   /// </summary>
   private static StateStatus? Settled(int found, int supported)
   {
      if(found < 0)
      {
         throw new InvalidDataException("The print journal carries a schema version no version of this application writes; it was not changed.");
      }
      if(found > supported)
      {
         return new StateStatus.NewerBuild(found, supported);
      }
      return found == supported ? new StateStatus.Ready() : null;
   }



   /// <summary>
   /// Opens a store's connection to the journal in a state folder and checks that the journal is at this build's schema
   /// version. A missing journal, or one behind the version, has not been migrated, and one ahead of it belongs to a newer
   /// build; either way the connection is closed and the caller is told which. Nothing is created and nothing is written.
   /// </summary>
   internal static SqliteConnection OpenCurrent(string directory)
   {
      if(!File.Exists(JournalDatabase.PathOf(directory)))
      {
         throw new InvalidOperationException(NotMigratedText);
      }
      SqliteConnection connection = JournalDatabase.Open(directory);
      try
      {
         using SqliteCommand command = connection.CreateCommand();
         command.CommandText = "PRAGMA user_version";
         int found = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
         if(found != Version)
         {
            throw new InvalidOperationException(found > Version ? StateStatus.NewerBuildText : NotMigratedText);
         }
         return connection;
      }
      catch
      {
         connection.Dispose();
         throw;
      }
   }



   /// <summary>
   /// Runs the steps a journal has not passed, all in one transaction, and stamps the new version in that same
   /// transaction. A step that fails rolls back every step of this run and the version with them, so the journal is as
   /// it was and the next start tries again. The connection is not pooled: it is closed, and the file released, the
   /// moment the migration ends. Write-ahead logging is switched on before the transaction because SQLite cannot change
   /// the journal mode inside one; it is a property of the file, set once here for every later connection.
   /// <para>
   /// The transaction takes SQLite's write lock when it begins, and the version is read again under that lock. That
   /// reading, not the caller's earlier one, decides which steps run. The journal's lock keeps the callers of this
   /// process apart; a second process does not hold it, and could have migrated the journal between the caller's reading
   /// and this transaction. The steps it ran are then not run a second time, and when it left a version above this
   /// build's, the journal is reported as a newer build's and nothing is written to it.
   /// </para>
   /// </summary>
   private static StateStatus Apply(string folder, IReadOnlyList<Action<JournalMigrationContext>> steps)
   {
      using SqliteConnection connection = JournalDatabase.OpenUnpooled(folder);
      using SqliteCommand command = connection.CreateCommand();
      command.CommandText = "PRAGMA journal_mode=WAL";
      _ = command.ExecuteScalar();
      using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
      command.Transaction = transaction;
      JournalMigrationContext context = new(command, folder);
      int found = Convert.ToInt32(context.Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
      if(Settled(found, steps.Count) is StateStatus settled)
      {
         return settled;
      }
      for(int step = found; step < steps.Count; step++)
      {
         steps[step](context);
      }
      /////////////////////////////////////////////////////////////////////
      // PRAGMA takes no parameter, so the version is written into the   //
      // text. It is a count of steps, formatted here, never input.      //
      /////////////////////////////////////////////////////////////////////
      context.Execute("PRAGMA user_version=" + steps.Count.ToString(CultureInfo.InvariantCulture));
      transaction.Commit();
      return new StateStatus.Ready();
   }
}
