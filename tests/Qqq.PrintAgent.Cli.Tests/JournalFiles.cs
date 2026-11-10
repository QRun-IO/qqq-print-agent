/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Data.Sqlite;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Cleanup of the journals a test created. A closed store leaves its SQLite connection in a pool, which keeps the journal file
/// open, so the folder cannot be deleted until that pool is emptied. Only the pools of the test's own journals are emptied
/// here. <c>SqliteConnection.ClearAllPools</c> must not be used in this suite: tests run side by side, and clearing every
/// pool disposes the connection a store in another test has just taken from its pool and is about to use, which that test
/// then sees as <c>ObjectDisposedException: SQLitePCL.sqlite3</c> while it opens its store.
/// </summary>
internal static class JournalFiles
{
   /// <summary>Closes the idle pooled connections of the journal in each given folder, spelled as the stores that opened it spelled it, and of no other journal.</summary>
   internal static void Release(params string[] directories)
   {
      foreach(string directory in directories)
      {
         using var journal = new SqliteConnection(TestState.JournalConnectionString(directory));
         SqliteConnection.ClearPool(journal);
      }
   }



   /// <summary>
   /// Releases the journal in the folder and every journal in a folder beneath it, then deletes the folder. A journal that was
   /// also opened under another spelling of its folder (another letter case, a trailing separator) has a pool per spelling;
   /// those spellings are passed as well. A folder that was never created is left alone.
   /// </summary>
   internal static void Delete(string directory, params string[] otherSpellings)
   {
      Release(otherSpellings);
      if(!Directory.Exists(directory))
      { return; }
      Release([directory, .. Directory.EnumerateFiles(directory, "journal.db", SearchOption.AllDirectories).Select(file => Path.GetDirectoryName(file)!)]);
      Directory.Delete(directory, recursive: true);
   }
}
