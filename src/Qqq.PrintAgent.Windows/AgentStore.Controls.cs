using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentStore
{
   private void InitializeControls()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = """
         CREATE TABLE IF NOT EXISTS control_state(connection_id TEXT PRIMARY KEY,json TEXT NOT NULL,updated_at TEXT NOT NULL);
         CREATE TABLE IF NOT EXISTS control_commands(connection_id TEXT NOT NULL,command_id TEXT NOT NULL,command_json TEXT NOT NULL,receipt_json TEXT,acknowledged INTEGER NOT NULL DEFAULT 0,expires_at TEXT NOT NULL,PRIMARY KEY(connection_id,command_id));
         CREATE INDEX IF NOT EXISTS control_outbox ON control_commands(connection_id,acknowledged);
         CREATE INDEX IF NOT EXISTS control_expiry ON control_commands(expires_at);
         """;
      command.ExecuteNonQuery();
   }

   public AgentControlState LoadControlState()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT json FROM control_state WHERE connection_id=$scope";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<AgentControlState>(json, PrintApiClient.JsonOptions) ?? AgentControlState.Initial : AgentControlState.Initial;
   }

   public void SaveControlState(AgentControlState state)
   {
      using SqliteTransaction transaction = _database.BeginTransaction();
      using SqliteCommand command = _database.CreateCommand();
      command.Transaction = transaction;
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToString("O", CultureInfo.InvariantCulture));
      command.CommandText = "DELETE FROM control_state WHERE connection_id<>$scope AND updated_at<$cutoff AND NOT EXISTS(SELECT 1 FROM control_commands c WHERE c.connection_id=control_state.connection_id AND c.acknowledged=0)";
      command.ExecuteNonQuery();
      command.CommandText = "SELECT COUNT(*) FROM control_state WHERE connection_id<>$scope";
      if(Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) >= 128)
      {
         throw new InvalidOperationException("Control connection history is full; review retained connection state before proceeding.");
      }
      command.CommandText = "INSERT INTO control_state(connection_id,json,updated_at) VALUES($scope,$json,$now) ON CONFLICT(connection_id) DO UPDATE SET json=$json,updated_at=$now";
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state, PrintApiClient.JsonOptions));
      command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
      command.ExecuteNonQuery();
      transaction.Commit();
   }

   /// <summary>Commit the execution marker before any effect. An existing marker must never be executed again.</summary>
   public bool BeginControlCommand(DeviceControlCommand value)
   {
      using SqliteTransaction transaction = _database.BeginTransaction();
      using SqliteCommand command = _database.CreateCommand();
      command.Transaction = transaction;
      command.CommandText = "SELECT command_json FROM control_commands WHERE connection_id=$scope AND command_id=$id";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$id", value.CommandId);
      string json = JsonSerializer.Serialize(value, PrintApiClient.JsonOptions);
      if(command.ExecuteScalar() is string existing)
      {
         if(!string.Equals(existing, json, StringComparison.Ordinal))
         {
            throw new InvalidDataException("A control command identity was reused with different content.");
         }
         return false;
      }
      command.CommandText = "DELETE FROM control_commands WHERE expires_at<$cutoff AND (acknowledged<>0 OR connection_id<>$scope)";
      command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToString("O", CultureInfo.InvariantCulture));
      command.ExecuteNonQuery();
      command.CommandText = "SELECT COUNT(*) FROM control_commands";
      if(Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) >= 4096)
      {
         throw new InvalidOperationException("Control receipt journal is full; resolve delivery before accepting more commands.");
      }
      command.CommandText = "INSERT INTO control_commands(connection_id,command_id,command_json,expires_at) VALUES($scope,$id,$json,$expires)";
      command.Parameters.AddWithValue("$json", json);
      command.Parameters.AddWithValue("$expires", DateTimeOffset.Parse(value.ExpiresAt, CultureInfo.InvariantCulture).ToString("O", CultureInfo.InvariantCulture));
      command.ExecuteNonQuery();
      transaction.Commit();
      return true;
   }

   public DeviceControlReceipt? GetControlReceipt(string commandId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT receipt_json FROM control_commands WHERE connection_id=$scope AND command_id=$id";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$id", commandId);
      return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<DeviceControlReceipt>(json, PrintApiClient.JsonOptions) : null;
   }

   public void SaveControlReceipt(string commandId, DeviceControlReceipt receipt)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "UPDATE control_commands SET receipt_json=$json WHERE connection_id=$scope AND command_id=$id AND receipt_json IS NULL";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$id", commandId);
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt, PrintApiClient.JsonOptions));
      command.ExecuteNonQuery();
   }

   public IReadOnlyList<(string CommandId, DeviceControlReceipt Receipt)> PendingControlReceipts()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT command_id,receipt_json FROM control_commands WHERE connection_id=$scope AND acknowledged=0 AND receipt_json IS NOT NULL ORDER BY expires_at,command_id LIMIT 20";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      using SqliteDataReader reader = command.ExecuteReader();
      var result = new List<(string, DeviceControlReceipt)>();
      while(reader.Read())
      {
         result.Add((reader.GetString(0), JsonSerializer.Deserialize<DeviceControlReceipt>(reader.GetString(1), PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Invalid saved control receipt.")));
      }
      return result;
   }

   public void RecoverInterruptedControlCommands()
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT command_id FROM control_commands WHERE connection_id=$scope AND receipt_json IS NULL ORDER BY expires_at,command_id LIMIT 20";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      var identifiers = new List<string>();
      using(SqliteDataReader reader = command.ExecuteReader())
      {
         while(reader.Read())
         { identifiers.Add(reader.GetString(0)); }
      }
      foreach(string identifier in identifiers)
      {
         SaveControlReceipt(identifier, new DeviceControlReceipt(Guid.NewGuid().ToString("N"), "OUTCOME_UNKNOWN",
            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), "Execution was interrupted after its durable intent. The action was not repeated; inspect its outcome."));
      }
   }

   public void AcknowledgeControlReceipt(string commandId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "UPDATE control_commands SET acknowledged=1 WHERE connection_id=$scope AND command_id=$id";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$id", commandId);
      command.ExecuteNonQuery();
   }

   /// <summary>Transport retirement is not server acknowledgement; preserve the exact outcome locally.</summary>
   public bool RetireExpiredControlReceipt(string commandId)
   {
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "UPDATE control_commands SET acknowledged=2 WHERE connection_id=$scope AND command_id=$id AND receipt_json IS NOT NULL AND expires_at<$cutoff";
      command.Parameters.AddWithValue("$scope", _connectionScope);
      command.Parameters.AddWithValue("$id", commandId);
      command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-7).ToString("O", CultureInfo.InvariantCulture));
      return command.ExecuteNonQuery() == 1;
   }
}
