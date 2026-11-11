/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The journal file of a state folder and the ways it is opened: by a store, from the journal's pool and under the
/// journal's lock, and without the pool, by a test that needs SQL of its own. It also owns the lock of each folder's
/// journal. Nothing here knows what the journal contains.
/// </summary>
internal static class JournalDatabase
{
   /// <summary>The journal's file name inside the state folder.</summary>
   internal const string FileName = "journal.db";



   private static readonly ConcurrentDictionary<string, Lock> Locks = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>Selects the Windows system SQLite library once, so the agent ships no native SQLite binary of its own. Every way of opening a journal passes through this type, so the library is selected before the first one.</summary>
   static JournalDatabase() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());



   /// <summary>
   /// The lock of the journal in a folder. Every store this process opens on that folder takes it while it opens its
   /// connection and while it creates or alters the schema, so two of them cannot both add a column or a table.
   /// Journals in different folders do not wait for each other: they are separate files, and a single lock for the whole
   /// process made every open queue behind every other one. The lock is found by the folder's full path without a
   /// trailing separator, compared without regard to letter case. That covers one path written relative or absolute, in
   /// another letter case, or with a trailing separator. It does not cover other routes to the same folder: a short (8.3)
   /// name, a junction or a substituted drive letter each get a lock of their own.
   /// </summary>
   internal static Lock LockFor(string directory)
      => Locks.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), static _ => new Lock());



   /// <summary>The full path of the journal file in a folder.</summary>
   internal static string PathOf(string directory) => Path.Combine(Path.GetFullPath(directory), FileName);



   /// <summary>
   /// The connection string every store uses for the journal in a folder. SQLite keeps one pool of idle connections per
   /// connection string, so this text also names the journal's pool; a test that must delete a journal releases that pool by
   /// it, and no other.
   /// </summary>
   internal static string ConnectionString(string directory)
      => new SqliteConnectionStringBuilder { DataSource = PathOf(directory) }.ToString();



   /// <summary>
   /// Opens a store's connection to the journal in a folder, from the journal's pool, while holding the journal's lock.
   /// The caller owns the connection. One that could not be opened is disposed before the failure is passed on.
   /// </summary>
   internal static SqliteConnection Open(string directory)
   {
      SqliteConnection connection = new(ConnectionString(directory));
      try
      {
         /////////////////////////////////////////////////////////////////////
         // Open must stay inside this lock. Microsoft.Data.Sqlite 10.0.0   //
         // takes a pooled connection and only afterwards, outside the      //
         // pool's own lock, records which SqliteConnection owns it. When   //
         // another thread opens the same journal in that gap and finds     //
         // the pool empty, the pool takes the first connection for         //
         // abandoned and hands it to the second thread as well. Both       //
         // stores then share one native handle: one store's transaction    //
         // can swallow or roll back the other's writes. Every pooled open  //
         // of one journal goes through this method, and the lock is at     //
         // least as wide as the pool, so with Open inside it no opener     //
         // can be in that gap while another asks the pool.                 //
         /////////////////////////////////////////////////////////////////////
         lock(LockFor(directory))
         {
            connection.Open();
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
   /// Opens a connection to the journal in a folder that is not pooled, creating the file when it is missing, for a test
   /// that needs SQL of its own. Unpooled, it has a native handle that no store can be handed by the pool, and closing it
   /// releases the file at once.
   /// </summary>
   internal static SqliteConnection OpenUnpooled(string directory)
   {
      SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = PathOf(directory), Pooling = false }.ToString());
      try
      {
         connection.Open();
         return connection;
      }
      catch
      {
         connection.Dispose();
         throw;
      }
   }
}
