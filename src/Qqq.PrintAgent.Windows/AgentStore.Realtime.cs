/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Realtime settings part of the store. Settings are kept per connection scope, so switching to another server or account never applies
/// the previous connection's transport choices.
/// </summary>
public sealed partial class AgentStore
{
   /// <summary>Reads this connection's settings, falling back to defaults when none are saved, and validates them so a damaged saved value is rejected rather than used.</summary>
   public RealtimeSettings LoadRealtimeSettings()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "CREATE TABLE IF NOT EXISTS realtime_settings(connection_id TEXT PRIMARY KEY,json TEXT NOT NULL); SELECT json FROM realtime_settings WHERE connection_id=$scope";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      RealtimeSettings settings = command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<RealtimeSettings>(json, PrintApiClient.JsonOptions) ?? new() : new();
      settings.Validate();
      return settings;
   }



   /// <summary>Validates and then upserts this connection's settings, so an invalid combination is never persisted.</summary>
   public void SaveRealtimeSettings(RealtimeSettings settings)
   {
      settings.Validate();
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "CREATE TABLE IF NOT EXISTS realtime_settings(connection_id TEXT PRIMARY KEY,json TEXT NOT NULL); INSERT INTO realtime_settings(connection_id,json) VALUES($scope,$json) ON CONFLICT(connection_id) DO UPDATE SET json=$json";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, PrintApiClient.JsonOptions));
      command.ExecuteNonQuery();
   }
}
