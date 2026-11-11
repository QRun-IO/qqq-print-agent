/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// What one migration step works with: the journal, through a command that belongs to the migration's one transaction,
/// and the state folder the journal lives in. A step never opens a connection or a transaction of its own, so everything
/// it does is committed or rolled back together with the steps around it. Later steps need more than the journal (what
/// the previous version left in the folder, the time of the migration); those are added here, not to each step.
/// </summary>
internal sealed class JournalMigrationContext(SqliteCommand command, string directory)
{
   /// <summary>The state folder whose journal is being migrated, as a full path.</summary>
   internal string Directory => directory;



   /// <summary>Runs SQL that returns nothing, inside the migration's transaction. Values are passed as named parameters and never written into the text.</summary>
   internal void Execute(string sql, params (string Name, object Value)[] parameters)
   {
      Prepare(sql, parameters);
      _ = command.ExecuteNonQuery();
   }



   /// <summary>Runs a query that returns one whole number, inside the migration's transaction.</summary>
   internal long Scalar(string sql, params (string Name, object Value)[] parameters)
   {
      Prepare(sql, parameters);
      return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
   }



   /// <summary>True when the journal has a table of this name.</summary>
   internal bool HasTable(string table) => Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table", ("$table", table)) != 0;



   /// <summary>True when a table of the journal has a column of this name; false also when the table does not exist.</summary>
   internal bool HasColumn(string table, string column)
      => Scalar("SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$column", ("$table", table), ("$column", column)) != 0;



   /// <summary>Puts the text and the parameters of the next statement on the one command, replacing those of the statement before.</summary>
   private void Prepare(string sql, (string Name, object Value)[] parameters)
   {
      command.CommandText = sql;
      command.Parameters.Clear();
      foreach((string name, object value) in parameters)
      {
         _ = command.Parameters.AddWithValue(name, value);
      }
   }
}
