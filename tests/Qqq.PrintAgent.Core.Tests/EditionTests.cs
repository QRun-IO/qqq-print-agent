/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>A compiled-in client edition is read strictly, applies presentation only, and is absent for the generic build.</summary>
public sealed class EditionTests
{
   /// <summary>A complete descriptor yields the client's names, support link, accent and asset prefix, and applying it changes presentation but not identity.</summary>
   [Fact]
   public void ValidEdition_IsLoadedAndAppliedToPresentationOnly()
   {
      string path = Path.Combine(Path.GetTempPath(), "qqq-edition-" + Guid.NewGuid().ToString("N") + ".json");
      try
      {
         File.WriteAllText(path, """{"id":"acme-print","displayName":"Acme Print Connector","companyName":"Acme Logistics","tagline":"Printing for Acme","supportUrl":"https://support.example.invalid/print","accentColor":"#0F766E","assetPrefix":"AcmePrintConnector"}""");
         var edition = Edition.Load(path);
         Assert.NotNull(edition);
         Assert.Equal("acme-print", edition.Id);
         Assert.Equal("Acme Print Connector", edition.DisplayName);
         Assert.Equal("Acme Logistics", edition.CompanyName);
         Assert.Equal("AcmePrintConnector", edition.AssetPrefix);
         Assert.Equal("#0F766E", edition.AccentColor);
         Assert.Equal(new Uri("https://support.example.invalid/print"), edition.SupportUrl);
         KofTwentyTwo.AppKit.AppInfo applied = edition.Apply(AppIdentity.BaseInfo);
         Assert.Equal("Acme Print Connector", applied.DisplayName);
         Assert.Equal("Printing for Acme", applied.Tagline);
         Assert.Equal("#0F766E", applied.Brand.GradientStart);
         Assert.Equal(AppIdentity.BaseInfo.Id, applied.Id);
         Assert.Equal(AppIdentity.BaseInfo.Author, applied.Author);
         Assert.Equal(AppIdentity.BaseInfo.RepositoryUrl, applied.RepositoryUrl);
         Assert.Equal(AppIdentity.BaseInfo.Attributions, applied.Attributions);
      }
      finally
      {
         File.Delete(path);
      }
   }



   /// <summary>A missing file is the generic build; a descriptor with a bad id, a bad asset prefix, missing names or the wrong shape is ignored as a whole, and an unreadable accent is dropped.</summary>
   [Theory]
   [InlineData("""{"id":"Bad Id","displayName":"X","companyName":"Y","assetPrefix":"XY"}""", false)]
   [InlineData("""{"id":"ok","displayName":"X","companyName":"Y","assetPrefix":"has-hyphen"}""", false)]
   [InlineData("""{"id":"ok","displayName":"X","assetPrefix":"XY"}""", false)]
   [InlineData("""["not","an","object"]""", false)]
   [InlineData("""{"id":"ok","displayName":"X","companyName":"Y","assetPrefix":"XY","accentColor":"#FFFF00"}""", true)]
   public void InvalidEdition_IsIgnoredAndUnreadableAccentDropped(string json, bool loads)
   {
      string path = Path.Combine(Path.GetTempPath(), "qqq-edition-" + Guid.NewGuid().ToString("N") + ".json");
      try
      {
         File.WriteAllText(path, json);
         var edition = Edition.Load(path);
         Assert.Equal(loads, edition is not null);
         if(edition is not null)
         {
            Assert.Null(edition.AccentColor);
         }
      }
      finally
      {
         File.Delete(path);
      }
      Assert.Null(Edition.Load(Path.Combine(Path.GetTempPath(), "qqq-edition-missing-" + Guid.NewGuid().ToString("N") + ".json")));
   }



   /// <summary>The test host carries no edition, so the identity is the generic one and the updater follows the generic asset name.</summary>
   [Fact]
   public void GenericBuild_HasNoEdition()
   {
      Assert.Null(AppIdentity.Edition);
      Assert.Equal(Edition.DefaultAssetPrefix, AppIdentity.UpdateAssetPrefix);
      Assert.Equal("QQQ Print Agent", AppIdentity.DefaultInfo.DisplayName);
   }
}
