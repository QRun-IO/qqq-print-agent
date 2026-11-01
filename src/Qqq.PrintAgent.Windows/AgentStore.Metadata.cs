/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Local name, description and location part of the store. These are the operator's own labels for the computer and each
/// printer; they live in one small key/value table, are bounded and free of control characters on the way in, and are
/// synchronized to QQQ by the service rather than being authoritative there.
/// </summary>
public sealed partial class AgentStore
{
   /// <summary>Returns the saved computer profile, or an empty one when none was saved, so callers never handle a missing profile.</summary>
   public DeviceProfile GetDeviceProfile() => ReadProfile<DeviceProfile>("device") ?? new DeviceProfile();



   /// <summary>Returns the saved profile of one printer, or an empty one when none was saved.</summary>
   public PrinterProfile GetPrinterProfile(string printerId) => ReadProfile<PrinterProfile>(PrinterProfileKey(printerId)) ?? new PrinterProfile();



   /// <summary>Saves the computer profile after trimming each field and enforcing its length (512, 4096 for the description) and the no-control-character rule, so an oversized or hostile value is refused instead of stored.</summary>
   public void SaveDeviceProfile(DeviceProfile profile)
   {
      ArgumentNullException.ThrowIfNull(profile);
      WriteProfile("device", new DeviceProfile(ProfileText(profile.Name, 512), ProfileText(profile.Description, 4096, multiline: true), ProfileText(profile.Location, 512)));
   }



   /// <summary>Saves one printer's display name, description and location with the same validation as the computer profile, keyed by a bounded identifier.</summary>
   public void SavePrinterProfile(string printerId, PrinterProfile profile)
   {
      ArgumentNullException.ThrowIfNull(profile);
      WriteProfile(PrinterProfileKey(printerId), new PrinterProfile(ProfileText(profile.DisplayName, 512), ProfileText(profile.Description, 4096, multiline: true), ProfileText(profile.Location, 512)));
   }



   /// <summary>Reads one stored profile by key as JSON, creating the table first when this is a store from before profiles existed; a missing key yields the default.</summary>
   private T? ReadProfile<T>(string key)
   {
      EnsureMetadataTable();
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "SELECT json FROM local_metadata WHERE profile_key=$key";
      command.Parameters.AddWithValue("$key", key);
      return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, PrintApiClient.JsonOptions) : default;
   }



   /// <summary>Inserts or replaces one profile as JSON in a single statement, so a save is all-or-nothing.</summary>
   private void WriteProfile<T>(string key, T profile)
   {
      EnsureMetadataTable();
      using SqliteCommand command = _database.CreateCommand();
      command.CommandText = "INSERT INTO local_metadata(profile_key,json) VALUES($key,$json) ON CONFLICT(profile_key) DO UPDATE SET json=excluded.json";
      command.Parameters.AddWithValue("$key", key);
      command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile, PrintApiClient.JsonOptions));
      command.ExecuteNonQuery();
   }



   /// <summary>Creates the profile table on first use under the shared schema lock, so concurrent stores cannot race creating it.</summary>
   private void EnsureMetadataTable()
   {
      lock(SchemaLock)
      {
         using SqliteCommand command = _database.CreateCommand();
         command.CommandText = "CREATE TABLE IF NOT EXISTS local_metadata(profile_key TEXT PRIMARY KEY,json TEXT NOT NULL)";
         command.ExecuteNonQuery();
      }
   }



   /// <summary>
   /// Trims a field and rejects it when it exceeds the maximum or contains control characters. Only the description may
   /// span lines (carriage return, line feed and tab are allowed there), so single-line fields cannot smuggle line breaks into logs or tables.
   /// </summary>
   private static string ProfileText(string? value, int maximum, bool multiline = false)
   {
      string normalized = value?.Trim() ?? string.Empty;
      if(normalized.Length > maximum || normalized.Any(character => char.IsControl(character) && !(multiline && character is '\r' or '\n' or '\t')))
      {
         throw new ArgumentException($"Metadata must contain at most {maximum} characters and no control characters.", nameof(value));
      }
      return normalized;
   }



   /// <summary>Builds the storage key for a printer ("printer:" plus its identifier), refusing a blank or over-long identifier so keys stay bounded and unambiguous.</summary>
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
