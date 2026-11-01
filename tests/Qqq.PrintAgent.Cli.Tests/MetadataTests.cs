/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class MetadataTests : IDisposable
{
   private readonly string _directory = Path.Combine(Path.GetTempPath(), "qqq-metadata-" + Guid.NewGuid().ToString("N"));

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

   [Fact]
   public void ExplicitClear_DiffersFromUnconfiguredMetadata()
   {
      using AgentStore store = new(_directory);
      Assert.Null(store.GetDeviceProfile().Name);
      store.SaveDeviceProfile(new DeviceProfile("Old name", "Old description", "Old location"));
      store.SaveDeviceProfile(new DeviceProfile("", "", ""));
      Assert.Equal(new DeviceProfile("", "", ""), store.GetDeviceProfile());
   }

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

   [Fact]
   public void NamesAndLocations_RejectEmbeddedLineBreaks()
   {
      using AgentStore store = new(_directory);
      Assert.Throws<ArgumentException>(() => store.SaveDeviceProfile(new DeviceProfile("First\nSecond")));
      Assert.Throws<ArgumentException>(() => store.SavePrinterProfile("queue", new PrinterProfile(Location: "First\tSecond")));
   }

   public void Dispose()
   {
      SqliteConnection.ClearAllPools();
      if(Directory.Exists(_directory))
      {
         Directory.Delete(_directory, recursive: true);
      }
   }
}
