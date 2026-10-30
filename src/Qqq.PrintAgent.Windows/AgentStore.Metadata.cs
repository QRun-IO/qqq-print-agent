using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentStore
{
   public DeviceProfile GetDeviceProfile() => ReadProfile<DeviceProfile>("device") ?? new DeviceProfile();

   public PrinterProfile GetPrinterProfile(string printerId) => ReadProfile<PrinterProfile>(PrinterProfileKey(printerId)) ?? new PrinterProfile();

   public void SaveDeviceProfile(DeviceProfile profile)
   {
      ArgumentNullException.ThrowIfNull(profile);
      WriteProfile("device", new DeviceProfile(ProfileText(profile.Name, 512), ProfileText(profile.Description, 4096, multiline: true), ProfileText(profile.Location, 512)));
   }

   public void SavePrinterProfile(string printerId, PrinterProfile profile)
   {
      ArgumentNullException.ThrowIfNull(profile);
      WriteProfile(PrinterProfileKey(printerId), new PrinterProfile(ProfileText(profile.DisplayName, 512), ProfileText(profile.Description, 4096, multiline: true), ProfileText(profile.Location, 512)));
   }

   private T? ReadProfile<T>(string key)
   {
      EnsureMetadataTable();
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT json FROM local_metadata WHERE profile_key=$key";
      command.Parameters.AddWithValue("$key", key);
      return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, PrintApiClient.JsonOptions) : default;
   }

   private void WriteProfile<T>(string key, T profile)
   {
      EnsureMetadataTable();
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "INSERT INTO local_metadata(profile_key,json) VALUES($key,$json) ON CONFLICT(profile_key) DO UPDATE SET json=excluded.json";
      command.Parameters.AddWithValue("$key", key);
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile, PrintApiClient.JsonOptions));
      command.ExecuteNonQuery();
   }

   private void EnsureMetadataTable()
   {
      lock(SchemaLock)
      {
         using SqliteCommand command = _database.CreateCommand();
         command.CommandText = "CREATE TABLE IF NOT EXISTS local_metadata(profile_key TEXT PRIMARY KEY,json TEXT NOT NULL)";
         command.ExecuteNonQuery();
      }
   }

   private static string ProfileText(string? value, int maximum, bool multiline = false)
   {
      string normalized = value?.Trim() ?? string.Empty;
      if(normalized.Length > maximum || normalized.Any(character => char.IsControl(character) && !(multiline && character is '\r' or '\n' or '\t')))
      {
         throw new ArgumentException($"Metadata must contain at most {maximum} characters and no control characters.", nameof(value));
      }
      return normalized;
   }

   private static string PrinterProfileKey(string printerId)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(printerId);
      if(printerId.Length > 1024)
      {
         throw new ArgumentException("Printer identifier is too long.", nameof(printerId));
      }
      return "printer:" + printerId;
   }
}
