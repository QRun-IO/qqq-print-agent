/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Operator-entered names, locations and descriptions for the computer and its printers: stored durably, validated at their limits, and kept apart from the technical queue identity the server dispatches on.</summary>
public sealed class MetadataTests : IDisposable
{
   private readonly string _directory = Path.Combine(Path.GetTempPath(), "qqq-metadata-" + Guid.NewGuid().ToString("N"));



   /// <summary>Profiles survive closing and reopening the store, surrounding spaces are trimmed, multi-line descriptions are kept, and a printer's friendly name is attached only to its own stable queue id, never to another.</summary>
   [Fact]
   public void Profiles_SurviveReopenAndRemainSeparateFromTechnicalQueueIdentity()
   {
      using(AgentStore store = new(_directory))
      {
         store.SaveDeviceProfile(new DeviceProfile(" Packing station 7 ", "First line\nSecond line", "West warehouse"));
         store.SavePrinterProfile("stable-queue-id", new PrinterProfile("Shipping labels", "Thermal printer", "Dock 2"));
      }
      using AgentStore reopened = new(_directory);
      Assert.Equal(new DeviceProfile("Packing station 7", "First line\nSecond line", "West warehouse"), reopened.GetDeviceProfile());
      Assert.Equal("Shipping labels", reopened.GetPrinterProfile("stable-queue-id").DisplayName);
      Assert.Null(reopened.GetPrinterProfile("different-queue-id").DisplayName);
   }



   /// <summary>Never having set a profile (null) is different from deliberately clearing it (empty text); the store keeps that difference so a cleared name is not replaced by a default.</summary>
   [Fact]
   public void ExplicitClear_DiffersFromUnconfiguredMetadata()
   {
      using AgentStore store = new(_directory);
      Assert.Null(store.GetDeviceProfile().Name);
      store.SaveDeviceProfile(new DeviceProfile("Old name", "Old description", "Old location"));
      store.SaveDeviceProfile(new DeviceProfile("", "", ""));
      Assert.Equal(new DeviceProfile("", "", ""), store.GetDeviceProfile());
   }



   /// <summary>A name or location of 513 characters, or a description of 4,097, is rejected, and the previously saved profile is left exactly as it was, so a bad edit can never half-apply.</summary>
   [Theory]
   [InlineData("name")]
   [InlineData("location")]
   [InlineData("description")]
   public void OversizedMetadata_IsRejectedWithoutChangingStoredProfile(string field)
   {
      using AgentStore store = new(_directory);
      DeviceProfile previous = new("Original", "Description", "Location");
      store.SaveDeviceProfile(previous);
      DeviceProfile invalid = field switch
      {
         "name" => previous with { Name = new string('x', 513) },
         "location" => previous with { Location = new string('x', 513) },
         _ => previous with { Description = new string('x', 4097) },
      };
      Assert.Throws<ArgumentException>(() => store.SaveDeviceProfile(invalid));
      Assert.Equal(previous, store.GetDeviceProfile());
   }



   /// <summary>Single-line fields (a name or location) refuse embedded line breaks and tabs, which would break one-line displays and lists; only the multi-line description may contain them.</summary>
   [Fact]
   public void NamesAndLocations_RejectEmbeddedLineBreaks()
   {
      using AgentStore store = new(_directory);
      Assert.Throws<ArgumentException>(() => store.SaveDeviceProfile(new DeviceProfile("First\nSecond")));
      Assert.Throws<ArgumentException>(() => store.SavePrinterProfile("queue", new PrinterProfile(Location: "First\tSecond")));
   }



   /// <summary>Releases SQLite's pooled connections and deletes the temporary store directory.</summary>
   public void Dispose()
   {
      SqliteConnection.ClearAllPools();
      if(Directory.Exists(_directory))
      {
         Directory.Delete(_directory, recursive: true);
      }
   }
}
