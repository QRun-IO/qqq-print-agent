/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security;
using KofTwentyTwo.AppKit;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// Where the app's presentation comes from: a machine branding file, the labels an installer persisted, and a compiled-in
/// edition. Each source is untrusted input, so these tests feed it malformed files and unusable labels, always from a
/// per-test temporary folder and a supplied label reader, never from the real ProgramData folder or registry.
/// </summary>
public sealed class BrandingSourceTests
{
   /// <summary>The installer's persisted labels fill in the names the branding file leaves out; the file always wins, and labels are trimmed.</summary>
   [Fact]
   public void InstallerLabelsFillInWhatTheBrandingFileLeavesOut()
   {
      using var temp = new TempDirectory();
      var labels = new Dictionary<string, string?>(StringComparer.Ordinal) { ["DisplayName"] = "  Installer Name  ", ["CompanyName"] = "Installer Company" };
      var fromLabels = RuntimeBranding.LoadMachine(temp.File("missing.json"), name => labels[name]);
      Assert.Equal("Installer Name", fromLabels.DisplayName);
      Assert.Equal("Installer Company", fromLabels.CompanyName);

      File.WriteAllText(temp.File("branding.json"), """{"displayName":"File Name"}""");
      var fromFile = RuntimeBranding.LoadMachine(temp.File("branding.json"), name => labels[name]);
      Assert.Equal("File Name", fromFile.DisplayName);
      Assert.Equal("Installer Company", fromFile.CompanyName);
      Assert.Equal(new RuntimeBranding(DisplayName: "File Name"), RuntimeBranding.LoadMachine(temp.File("branding.json"), null));
   }



   /// <summary>A persisted label that is blank, absent, over-long or carries control characters is ignored, so a damaged registry value cannot relabel the app.</summary>
   [Theory]
   [InlineData(null)]
   [InlineData("   ")]
   [InlineData("line one\nline two")]
   [InlineData("long")]
   public void UnusableInstallerLabelIsIgnored(string? label)
   {
      using var temp = new TempDirectory();
      string? value = string.Equals(label, "long", StringComparison.Ordinal) ? new string('n', 181) : label;
      Assert.Equal(new RuntimeBranding(), RuntimeBranding.LoadMachine(temp.File("missing.json"), _ => value));
   }



   /// <summary>When the installer's label store cannot be read, the branding file still applies and nothing fails.</summary>
   [Theory]
   [InlineData("security")]
   [InlineData("access")]
   [InlineData("io")]
   public void UnreadableInstallerLabelStoreLeavesTheBrandingFileInForce(string failure)
   {
      using var temp = new TempDirectory();
      File.WriteAllText(temp.File("branding.json"), """{"companyName":"File Company"}""");
      var branding = RuntimeBranding.LoadMachine(temp.File("branding.json"), _ => throw (failure switch
      {
         "security" => new SecurityException("Synthetic registry denial."),
         "access" => new UnauthorizedAccessException("Synthetic registry denial."),
         _ => new IOException("Synthetic registry failure."),
      }));
      Assert.Equal(new RuntimeBranding(CompanyName: "File Company"), branding);
   }



   /// <summary>
   /// A branding icon is used only when it is a local .ico file whose header and image directory are consistent with the
   /// file itself. Anything else, from a wrong extension to a directory entry pointing past the end of the file, is
   /// ignored and the built-in icon stays.
   /// </summary>
   [Theory]
   [InlineData("valid", true)]
   [InlineData("two-images", true)]
   [InlineData("wrong-extension", false)]
   [InlineData("network-share", false)]
   [InlineData("missing", false)]
   [InlineData("locked", false)]
   [InlineData("too-small", false)]
   [InlineData("reserved-not-zero", false)]
   [InlineData("not-an-icon-type", false)]
   [InlineData("no-images", false)]
   [InlineData("too-many-images", false)]
   [InlineData("truncated-directory", false)]
   [InlineData("empty-image", false)]
   [InlineData("image-inside-directory", false)]
   [InlineData("image-past-end", false)]
   public void BrandingIconIsUsedOnlyWhenItsStructureIsConsistent(string fault, bool accepted)
   {
      using var temp = new TempDirectory();
      string name = string.Equals(fault, "wrong-extension", StringComparison.Ordinal) ? "brand.png" : "brand.ico";
      byte[]? content = fault switch
      {
         "missing" => null,
         "too-small" => new byte[21],
         "two-images" => Icon(2, 1, 8, 38),
         "reserved-not-zero" => Icon(1, 1, 8, 22, reserved: 1),
         "not-an-icon-type" => Icon(1, 1, 8, 22, type: 2),
         "no-images" => Icon(0, 1, 8, 22, entries: 1),
         "too-many-images" => Icon(65, 1, 8, 22, entries: 1),
         "truncated-directory" => Icon(2, 1, 8, 22, entries: 1),
         "empty-image" => Icon(1, 1, 0, 22),
         "image-inside-directory" => Icon(1, 1, 8, 6),
         "image-past-end" => Icon(1, 1, 9, 22),
         _ => Icon(1, 1, 8, 22),
      };
      if(content is not null)
      { File.WriteAllBytes(temp.File(name), content); }
      string reference = string.Equals(fault, "network-share", StringComparison.Ordinal) ? "\\\\\\\\synthetic-server\\\\share\\\\brand.ico" : name;
      File.WriteAllText(temp.File("branding.json"), "{\"displayName\":\"Branded\",\"iconPath\":\"" + reference + "\"}");
      using FileStream? exclusive = string.Equals(fault, "locked", StringComparison.Ordinal) ? new FileStream(temp.File(name), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;

      var branding = RuntimeBranding.Load(temp.File("branding.json"));
      Assert.Equal("Branded", branding.DisplayName);
      Assert.Equal(accepted ? temp.File(name) : null, branding.IconPath);
      AppInfo applied = branding.Apply(AppIdentity.BaseInfo);
      Assert.Equal(accepted ? temp.File(name) : AppIdentity.BaseInfo.Brand.IconIcoPath, applied.Brand.IconIcoPath);
   }



   /// <summary>A branding logo with the wrong file type, one over two mebibytes, or one given as a drive-relative path whose meaning depends on the process, is ignored.</summary>
   [Theory]
   [InlineData("logo.jpg")]
   [InlineData("oversized.png")]
   [InlineData("C:logo.png")]
   public void BrandingLogoOfTheWrongKindIsIgnored(string reference)
   {
      using var temp = new TempDirectory();
      File.WriteAllBytes(temp.File("logo.jpg"), [1, 2, 3]);
      File.WriteAllBytes(temp.File("oversized.png"), new byte[2 * 1024 * 1024 + 1]);
      File.WriteAllText(temp.File("branding.json"), "{\"displayName\":\"Branded\",\"logoPath\":\"" + reference + "\"}");
      var branding = RuntimeBranding.Load(temp.File("branding.json"));
      Assert.Equal("Branded", branding.DisplayName);
      Assert.Null(branding.LogoPath);
   }



   /// <summary>An edition descriptor that is oversized, not valid JSON, or cannot be opened means the generic build, never a half-applied edition.</summary>
   [Theory]
   [InlineData("oversized")]
   [InlineData("malformed")]
   [InlineData("locked")]
   public void UnusableEditionDescriptorMeansTheGenericBuild(string fault)
   {
      const string valid = """{"id":"acme","displayName":"Acme Print","companyName":"Acme","assetPrefix":"AcmePrint"}""";
      using var temp = new TempDirectory();
      string path = temp.File(Edition.FileName);
      File.WriteAllText(path, valid);
      Assert.Equal("Acme Print", Edition.Load(path)!.DisplayName);
      File.WriteAllText(path, fault switch
      {
         "oversized" => valid.Replace("\"Acme\"", "\"" + new string('A', 17_000) + "\"", StringComparison.Ordinal),
         "malformed" => valid[..^1],
         _ => valid,
      });
      using FileStream? exclusive = string.Equals(fault, "locked", StringComparison.Ordinal) ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
      Assert.Null(Edition.Load(path));
   }



   /// <summary>
   /// The app's identity is the generic one, then the edition's presentation, then the machine's branding on top; the
   /// application id, which names the data folder and settings, never changes. Built here from an edition and branding
   /// of the test's own, so the result does not depend on what is installed on the machine running the tests.
   /// </summary>
   [Fact]
   public void IdentityLayersEditionThenMachineBrandingAndKeepsTheApplicationId()
   {
      var edition = new Edition("acme", "Acme Print", "Acme Corporation", null, null, new Uri("https://support.acme.invalid"), "#123456", "AcmePrint");
      AppInfo generic = AppIdentity.Compose(null, new RuntimeBranding());
      AppInfo editionOnly = AppIdentity.Compose(edition, new RuntimeBranding());
      AppInfo branded = AppIdentity.Compose(edition, new RuntimeBranding(DisplayName: "Site Print", CompanyName: "Site Operations"));

      Assert.Equal("QQQ Print Agent", generic.DisplayName);
      Assert.Equal("Acme Print", editionOnly.DisplayName);
      Assert.Equal("Acme Corporation", editionOnly.Tagline);
      Assert.Equal("#123456", editionOnly.Brand.GradientStart);
      Assert.Equal(new Uri("https://support.acme.invalid"), editionOnly.Website);
      Assert.Equal("Site Print", branded.DisplayName);
      Assert.Equal("Site Operations", branded.Tagline);
      Assert.Equal("#123456", branded.Brand.GradientStart);
      Assert.All(new[] { generic, editionOnly, branded }, info => Assert.Equal("qqq-print-agent", info.Id));
      Assert.Equal(AppIdentity.BaseInfo.Attributions, branded.Attributions);
   }



   /// <summary>Only an edition ships a logo beside the executable: the generic build has none, and an edition has one only when the file is actually there.</summary>
   [Fact]
   public void EditionLogoIsFoundOnlyForAnEditionThatShipsOne()
   {
      using var temp = new TempDirectory();
      var edition = new Edition("acme", "Acme Print", "Acme Corporation", null, null, null, null, "AcmePrint");
      Assert.Null(AppIdentity.EditionLogoIn(edition, temp.Path));
      Directory.CreateDirectory(Path.Combine(temp.Path, "Assets", "Brand"));
      File.WriteAllBytes(Path.Combine(temp.Path, "Assets", "Brand", "logo.png"), [1]);
      Assert.Equal(Path.Combine(temp.Path, "Assets", "Brand", "logo.png"), AppIdentity.EditionLogoIn(edition, temp.Path));
      Assert.Null(AppIdentity.EditionLogoIn(null, temp.Path));
      Assert.Null(AppIdentity.Edition);
      Assert.Null(AppIdentity.EditionLogoPath);
   }



   /// <summary>
   /// Builds an .ico file: the six byte header (reserved, type, image count) followed by sixteen byte directory entries
   /// that all claim the same image length and offset, padded to hold one eight byte image after a single entry.
   /// </summary>
   private static byte[] Icon(ushort count, int imageCount, uint length, uint offset, ushort reserved = 0, ushort type = 1, int? entries = null)
   {
      int directory = entries ?? count;
      byte[] file = new byte[6 + directory * 16 + imageCount * 8];
      BitConverter.TryWriteBytes(file.AsSpan(0), reserved);
      BitConverter.TryWriteBytes(file.AsSpan(2), type);
      BitConverter.TryWriteBytes(file.AsSpan(4), count);
      for(int index = 0; index < directory; index++)
      {
         BitConverter.TryWriteBytes(file.AsSpan(6 + index * 16 + 8), length);
         BitConverter.TryWriteBytes(file.AsSpan(6 + index * 16 + 12), offset);
      }
      return file;
   }
}
