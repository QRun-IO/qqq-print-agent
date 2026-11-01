/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>Machine branding changes presentation without changing deployment identity.</summary>
public sealed class RuntimeBrandingTests
{
   /// <summary>
   /// An accent color is accepted only if its exact, unrounded contrast ratio against white text is at least 4.5:1 (#767676 passes,
   /// #777777 does not), so a customer color can never make white button text unreadable. Rejection falls back to the default
   /// gradient rather than to a nearby color.
   /// </summary>
   [Theory]
   [InlineData("#FFFFFF", false)]
   [InlineData("#FFFF00", false)]
   [InlineData("#777777", false)]
   [InlineData("#767676", true)]
   [InlineData("#000000", true)]
   [InlineData("#0F766E", true)]
   public void AccentRequiresUnroundedFourPointFiveContrastWithWhite(string color, bool accepted)
   {
      using var temp = new TempDirectory();
      string path = temp.File("branding.json");
      File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { accentColor = color, displayName = "Customer name" }));
      var branding = RuntimeBranding.Load(path);
      Assert.Equal(accepted ? color : null, branding.AccentColor);
      Assert.Equal("Customer name", branding.DisplayName);
      string expected = accepted ? color : AppIdentity.DefaultInfo.Brand.GradientStart;
      Assert.Equal(expected, branding.Apply(AppIdentity.DefaultInfo).Brand.GradientStart);
      Assert.Equal(expected, new RuntimeBranding(AccentColor: color).Apply(AppIdentity.DefaultInfo).Brand.GradientStart);
   }



   /// <summary>
   /// Valid branding changes only what the customer sees (name, tagline, color, support link). The application id, copyright,
   /// repository and data-directory variable are deployment identity and attribution, and must come through unchanged.
   /// </summary>
   [Fact]
   public void ValidBrandingPreservesIdentityAndAttribution()
   {
      using var temp = new TempDirectory();
      string path = temp.File("branding.json");
      File.WriteAllText(path, """{"displayName":"Acme Print","companyName":"Acme","supportUrl":"https://support.example.com","accentColor":"#123ABC"}""");
      var branding = RuntimeBranding.Load(path);
      KofTwentyTwo.AppKit.AppInfo info = branding.Apply(AppIdentity.DefaultInfo);
      Assert.Equal("Acme Print", info.DisplayName);
      Assert.Equal("Acme", info.Tagline);
      Assert.Equal("#123ABC", info.Brand.GradientStart);
      Assert.Equal(new Uri("https://support.example.com"), branding.SupportUrl);
      Assert.Equal(AppIdentity.DefaultInfo.Id, info.Id);
      Assert.Equal(AppIdentity.DefaultInfo.Copyright, info.Copyright);
      Assert.Equal(AppIdentity.DefaultInfo.RepositoryUrl, info.RepositoryUrl);
      Assert.Equal(AppIdentity.DefaultInfo.DataDirectoryVariable, info.DataDirectoryVariable);
   }



   /// <summary>
   /// Malformed JSON, a non-object root, and an object whose every field is unsafe (wrong type, control character, plain-http
   /// support link, bad color, remote logo) all load as the default branding. A broken file must never stop the app or half-apply.
   /// </summary>
   [Theory]
   [InlineData("not json")]
   [InlineData("[]")]
   [InlineData("null")]
   [InlineData("{\"displayName\":42,\"companyName\":\"bad\\nname\",\"supportUrl\":\"http://example.com\",\"accentColor\":\"red\",\"logoPath\":\"https://example.com/a.png\"}")]
   public void InvalidSettingsFallBack(string json)
   {
      using var temp = new TempDirectory();
      string path = temp.File("branding.json");
      File.WriteAllText(path, json);
      Assert.Equal(new RuntimeBranding(), RuntimeBranding.Load(path));
   }



   /// <summary>An absent file and one just over the 16 KiB limit both yield the default branding, bounding what a machine file can cost to read.</summary>
   [Fact]
   public void MissingAndOversizedFilesFallBack()
   {
      using var temp = new TempDirectory();
      string path = temp.File("branding.json");
      Assert.Equal(new RuntimeBranding(), RuntimeBranding.Load(path));
      File.WriteAllText(path, new string(' ', 16 * 1024 + 1));
      Assert.Equal(new RuntimeBranding(), RuntimeBranding.Load(path));
   }



   /// <summary>
   /// A support link with embedded credentials, a local file, or a script scheme is dropped while the valid display name beside
   /// it is kept: the link is opened from the UI, so only safe web addresses may be offered.
   /// </summary>
   [Theory]
   [InlineData("https://user:password@example.com")]
   [InlineData("file:///C:/support.html")]
   [InlineData("javascript:alert(1)")]
   public void UnsafeSupportLinksAreIgnored(string support)
   {
      using var temp = new TempDirectory();
      string path = temp.File("branding.json");
      File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { supportUrl = support, displayName = "Valid name" }));
      var branding = RuntimeBranding.Load(path);
      Assert.Null(branding.SupportUrl);
      Assert.Equal("Valid name", branding.DisplayName);
   }



   /// <summary>
   /// A logo is used only when it is a real PNG file resolved beside the settings file. A file that is not an image, or a
   /// network (UNC) path, is rejected so the app never loads an arbitrary or remote file as an image.
   /// </summary>
   [Fact]
   public void OnlyLocalBoundedPngLogoIsAccepted()
   {
      using var temp = new TempDirectory();
      string path = temp.File("branding.json");
      string logo = temp.File("logo.png");
      File.WriteAllBytes(logo, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII="));
      File.WriteAllText(path, """{"logoPath":"logo.png"}""");
      Assert.Equal(logo, RuntimeBranding.Load(path).LogoPath);
      File.WriteAllText(logo, "invalid image");
      Assert.Null(RuntimeBranding.Load(path).LogoPath);
      File.WriteAllText(path, """{"logoPath":"\\\\server\\share\\logo.png"}""");
      Assert.Null(RuntimeBranding.Load(path).LogoPath);
   }



   /// <summary>Names of 181 characters exceed the installer's 180-character branding limit, so the whole file is ignored rather than truncated.</summary>
   [Fact]
   public void LabelsBeyondInstallerLimitAreIgnored()
   {
      using var temp = new TempDirectory();
      string path = temp.File("branding.json");
      File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { displayName = new string('x', 181), companyName = new string('x', 181) }));
      Assert.Equal(new RuntimeBranding(), RuntimeBranding.Load(path));
   }



   /// <summary>
   /// Names at exactly the 180-character limit are kept intact, and the description stays generic so a customer name never
   /// leaves the vendor's product name behind in descriptive text.
   /// </summary>
   [Fact]
   public void LongCustomerNamesArePreservedAndGenericDescriptionIsUsed()
   {
      using var temp = new TempDirectory();
      string path = temp.File("branding.json");
      string name = new('x', 180);
      File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { displayName = name, companyName = name }));
      var branding = RuntimeBranding.Load(path);
      Assert.Equal(name, branding.DisplayName);
      Assert.Equal(name, branding.CompanyName);
      Assert.DoesNotContain("QQQ", branding.Apply(AppIdentity.DefaultInfo).Description, StringComparison.Ordinal);
   }
}
