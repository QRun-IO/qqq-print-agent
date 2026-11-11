/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What the migration tests ask of a state folder from outside the product: the journal's schema version, its schema as
/// SQLite records it, a digest of each table's rows, and a hash of every file in the folder. The journal questions use
/// the test project's own unpooled connection, which closes the journal when it is done; closing the last connection
/// makes SQLite fold the write-ahead log into the journal file, so a test that compares file bytes takes its hashes
/// with <see cref="FileHashes"/> and does not ask a journal question in between.
/// </summary>
internal static class JournalProbe
{
   /// <summary>The journal's schema version (<c>PRAGMA user_version</c>).</summary>
   internal static int Version(string directory) => Convert.ToInt32(Scalar(directory, "PRAGMA user_version"), CultureInfo.InvariantCulture);



   /// <summary>
   /// The journal's schema: one line per table and index, with the statement that made it, in name order. SQLite keeps
   /// that text as the object was created plus any column added later, so two journals with equal lines have the same
   /// schema in every detail.
   /// </summary>
   internal static IReadOnlyList<string> Schema(string directory)
      => Rows(directory, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY name");



   /// <summary>The names of the journal's tables, in name order.</summary>
   internal static IReadOnlyList<string> Tables(string directory)
      => [.. Rows(directory, "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").Select(row => row["name=".Length..])];



   /// <summary>
   /// A SHA-256 digest of each table's rows, by table name. A row is its column names and values, so a renamed, added or
   /// removed column changes the digest as surely as a changed value does; the rows are put in order before they are
   /// hashed, so only their content counts and not where SQLite keeps them.
   /// </summary>
   internal static IReadOnlyDictionary<string, string> RowDigests(string directory)
   {
      SortedDictionary<string, string> digests = new(StringComparer.Ordinal);
      foreach(string table in Tables(directory))
      {
         //////////////////////////////////////////////////////////////////////////
         // the table's name comes from the journal's own catalog and is quoted, //
         // which is the one place a name, not a value, has to go into the text  //
         //////////////////////////////////////////////////////////////////////////
         List<string> rows = [.. Rows(directory, "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")];
         rows.Sort(StringComparer.Ordinal);
         digests[table] = rows.Count.ToString(CultureInfo.InvariantCulture) + " rows " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
      }
      return digests;
   }



   /// <summary>
   /// The SHA-256 of every file in the folder and beneath it, by path relative to the folder. The files are read with
   /// full sharing, so a file another handle has open can still be hashed, and nothing is opened for writing.
   /// </summary>
   internal static IReadOnlyDictionary<string, string> FileHashes(string directory)
   {
      SortedDictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
      foreach(string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
      {
         hashes[Path.GetRelativePath(directory, file)] = Convert.ToHexString(SHA256.HashData(ReadShared(file)));
      }
      return hashes;
   }



   /// <summary>The bytes of a file that another handle may have open for writing, as SQLite has its journal and log.</summary>
   internal static byte[] ReadShared(string file)
   {
      using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      using MemoryStream bytes = new();
      stream.CopyTo(bytes);
      return bytes.ToArray();
   }



   /// <summary>Runs a query that returns one value.</summary>
   internal static object? Scalar(string directory, string sql)
   {
      using SqliteConnection journal = TestState.OpenJournal(directory);
      using SqliteCommand command = journal.CreateCommand();
      command.CommandText = sql;
      return command.ExecuteScalar();
   }



   /// <summary>Runs a query and returns each row as one line of <c>column=value</c> pairs, a missing value written as <c>NULL</c>.</summary>
   internal static IReadOnlyList<string> Rows(string directory, string sql)
   {
      using SqliteConnection journal = TestState.OpenJournal(directory);
      using SqliteCommand command = journal.CreateCommand();
      command.CommandText = sql;
      using SqliteDataReader reader = command.ExecuteReader();
      List<string> rows = [];
      while(reader.Read())
      {
         rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(column
            => reader.GetName(column) + "=" + (reader.IsDBNull(column) ? "NULL" : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture)))));
      }
      return rows;
   }
}
