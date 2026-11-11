/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Transport trust part of the store. The setting is machine-wide rather than connection-scoped because it must apply to
/// the sign-in that happens before any pairing exists, and a damaged saved value falls back to the safe default (refuse).
/// </summary>
public sealed partial class AgentStore
{
   /// <summary>Reads the administrator's transport trust choice; a missing or unreadable value means untrusted certificates are refused.</summary>
   public ServerTrust LoadServerTrust()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT json FROM server_trust WHERE scope='machine'";
      try
      { return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<ServerTrust>(json, PrintApiClient.JsonOptions) ?? new() : new(); }
      catch(JsonException)
      { return new ServerTrust(); }
   }



   /// <summary>Saves the administrator's transport trust choice for both the service and the companion.</summary>
   public void SaveServerTrust(ServerTrust trust)
   {
      ArgumentNullException.ThrowIfNull(trust);
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "INSERT INTO server_trust(scope,json) VALUES('machine',$json) ON CONFLICT(scope) DO UPDATE SET json=$json";
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(trust, PrintApiClient.JsonOptions));
      command.ExecuteNonQuery();
   }
}
