/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Win32;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The real registry record, run against a private hive file in the test's temporary folder (see
/// <see cref="PrivateRegistryHive"/>), under the same key path the installed product uses. It pins the two value names
/// and kinds an installed product leaves in the registry, that a name written by a 1.2.x install is read as it is, what
/// a missing key or value means, and what happens to a value of a kind the record does not write. Only the last test
/// looks at the real registry, and it only opens the handle of the machine hive to see which one the factory binds.
/// </summary>
public sealed class RegistryShortcutRecordTests
{
   /// <summary>The key path under the base key: the installed product's.</summary>
   private const string Key = RegistryShortcutRecord.MachinePath;



   /// <summary>
   /// With no key, and with a key that holds neither value, nothing is recorded. Reading and forgetting create nothing:
   /// the key is still missing after every read and clear, which is what lets an uninstall with nothing recorded leave
   /// the registry untouched.
   /// </summary>
   [Fact]
   public void NothingIsRecordedWhenTheKeyOrTheValuesAreMissing()
   {
      using var hive = new PrivateRegistryHive();
      List<string> warnings = [];
      var record = new RegistryShortcutRecord(hive.Root, Key, warnings.Add);
      Assert.Null(record.ReadLastShortcutName());
      Assert.Empty(record.ReadPendingShortcutNames());
      record.ClearLastShortcutName();
      record.ClearPendingShortcutNames();
      Assert.Null(hive.Root.OpenSubKey("SOFTWARE"));
      hive.Root.CreateSubKey(Key).Dispose();
      Assert.Null(record.ReadLastShortcutName());
      Assert.Empty(record.ReadPendingShortcutNames());
      record.ClearLastShortcutName();
      record.ClearPendingShortcutNames();
      Assert.Empty(warnings);
   }



   /// <summary>Proving the record changeable creates the key when it is missing, writes no value, and can be repeated; a base key that does not allow writing is refused and nothing is created.</summary>
   [Fact]
   public void ProvingTheRecordChangeableCreatesTheKeyAndNothingElse()
   {
      using var hive = new PrivateRegistryHive();
      var record = new RegistryShortcutRecord(hive.Root, Key, _ => { });
      record.RequireChangeable();
      record.RequireChangeable();
      using(RegistryKey created = Assert.IsType<RegistryKey>(hive.Root.OpenSubKey(Key)))
      {
         Assert.Equal(0, created.ValueCount);
      }
      hive.Root.CreateSubKey("Closed").Dispose();
      using RegistryKey closed = Assert.IsType<RegistryKey>(hive.Root.OpenSubKey("Closed", writable: false));
      _ = Assert.Throws<UnauthorizedAccessException>(new RegistryShortcutRecord(closed, Key, _ => { }).RequireChangeable);
      Assert.Equal(0, closed.SubKeyCount);
   }



   /// <summary>
   /// Both notes are written under their exact names and kinds, a string and a multi-string, and read back as written,
   /// in order. Writing again replaces what was there; forgetting one leaves the other.
   /// </summary>
   [Fact]
   public void BothNotesAreWrittenUnderTheirExactNamesAndKindsAndReadBack()
   {
      using var hive = new PrivateRegistryHive();
      List<string> warnings = [];
      var record = new RegistryShortcutRecord(hive.Root, Key, warnings.Add);
      record.WriteLastShortcutName("Acme Print Connector.lnk");
      record.WritePendingShortcutNames(["Old Brand.lnk", "Größe & Söhne.lnk"]);
      using(RegistryKey key = Assert.IsType<RegistryKey>(hive.Root.OpenSubKey(@"SOFTWARE\QRunIO\PrintAgent")))
      {
         Assert.Equal(new[] { "LastShortcutName", "PendingShortcutNames" }, key.GetValueNames().Order(StringComparer.Ordinal), StringComparer.Ordinal);
         Assert.Equal(RegistryValueKind.String, key.GetValueKind("LastShortcutName"));
         Assert.Equal(RegistryValueKind.MultiString, key.GetValueKind("PendingShortcutNames"));
         Assert.Equal("Acme Print Connector.lnk", key.GetValue("LastShortcutName"));
         Assert.Equal(new[] { "Old Brand.lnk", "Größe & Söhne.lnk" }, Assert.IsType<string[]>(key.GetValue("PendingShortcutNames")), StringComparer.Ordinal);
      }
      Assert.Equal("Acme Print Connector.lnk", record.ReadLastShortcutName());
      Assert.Equal(new[] { "Old Brand.lnk", "Größe & Söhne.lnk" }, record.ReadPendingShortcutNames(), StringComparer.Ordinal);
      record.WriteLastShortcutName("Globex Connector.lnk");
      record.WritePendingShortcutNames(["Acme Print Connector.lnk"]);
      Assert.Equal("Globex Connector.lnk", record.ReadLastShortcutName());
      Assert.Equal(new[] { "Acme Print Connector.lnk" }, record.ReadPendingShortcutNames(), StringComparer.Ordinal);
      record.ClearPendingShortcutNames();
      Assert.Equal("Globex Connector.lnk", record.ReadLastShortcutName());
      Assert.Empty(record.ReadPendingShortcutNames());
      record.WritePendingShortcutNames(["Acme Print Connector.lnk"]);
      record.ClearLastShortcutName();
      Assert.Null(record.ReadLastShortcutName());
      Assert.Equal(new[] { "Acme Print Connector.lnk" }, record.ReadPendingShortcutNames(), StringComparer.Ordinal);
      Assert.Empty(warnings);
   }



   /// <summary>
   /// The key as a 1.2.x install left it, a string value naming the shortcut beside the installer's own label values,
   /// is read unchanged, with no other names. Recording other names afterwards leaves that value and the labels exactly
   /// as they were, so a 1.2.x product restored by a failed upgrade still finds its shortcut.
   /// </summary>
   [Fact]
   public void NameWrittenByAnEarlierVersionIsReadUnchangedAndLeftAsItIs()
   {
      using var hive = new PrivateRegistryHive();
      using(RegistryKey earlier = hive.Root.CreateSubKey(Key))
      {
         earlier.SetValue("DisplayName", "Acme Print Connector", RegistryValueKind.String);
         earlier.SetValue("CompanyName", "Acme", RegistryValueKind.String);
         earlier.SetValue("LastShortcutName", "Acme Print Connector.lnk", RegistryValueKind.String);
      }
      List<string> warnings = [];
      var record = new RegistryShortcutRecord(hive.Root, Key, warnings.Add);
      Assert.Equal("Acme Print Connector.lnk", record.ReadLastShortcutName());
      Assert.Empty(record.ReadPendingShortcutNames());
      record.RequireChangeable();
      record.WritePendingShortcutNames(["Acme Print Connector.lnk", "Globex Connector.lnk"]);
      record.ClearPendingShortcutNames();
      using RegistryKey key = Assert.IsType<RegistryKey>(hive.Root.OpenSubKey(Key));
      Assert.Equal(new[] { "CompanyName", "DisplayName", "LastShortcutName" }, key.GetValueNames().Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(RegistryValueKind.String, key.GetValueKind("LastShortcutName"));
      Assert.Equal("Acme Print Connector.lnk", key.GetValue("LastShortcutName"));
      Assert.Equal("Acme Print Connector", key.GetValue("DisplayName"));
      Assert.Empty(warnings);
   }



   /// <summary>
   /// A value of a kind the record does not write is never used: it reads as nothing recorded, and each such read gives
   /// one warning that names the value, so it is never passed over silently. The value itself is left as it is until
   /// the record is written, which replaces it with the right kind; after that it reads normally and nothing is warned.
   /// </summary>
   [Theory]
   [InlineData("LastShortcutName", RegistryValueKind.DWord)]
   [InlineData("LastShortcutName", RegistryValueKind.MultiString)]
   [InlineData("LastShortcutName", RegistryValueKind.Binary)]
   [InlineData("PendingShortcutNames", RegistryValueKind.String)]
   [InlineData("PendingShortcutNames", RegistryValueKind.ExpandString)]
   [InlineData("PendingShortcutNames", RegistryValueKind.QWord)]
   public void ValueOfAKindTheRecordDoesNotWriteIsNothingRecordedAndIsWarnedAbout(string name, RegistryValueKind kind)
   {
      using var hive = new PrivateRegistryHive();
      using(RegistryKey key = hive.Root.CreateSubKey(Key))
      {
         object value = kind switch
         {
            RegistryValueKind.DWord => 7,
            RegistryValueKind.QWord => 7L,
            RegistryValueKind.Binary => new byte[] { 1, 2, 3 },
            RegistryValueKind.MultiString => new[] { "Acme Print Connector.lnk" },
            _ => "Acme Print Connector.lnk",
         };
         key.SetValue(name, value, kind);
      }
      List<string> warnings = [];
      var record = new RegistryShortcutRecord(hive.Root, Key, warnings.Add);
      bool last = name is "LastShortcutName";
      Assert.Null(record.ReadLastShortcutName());
      Assert.Empty(record.ReadPendingShortcutNames());
      Assert.Equal(new[]
      {
         last
            ? "The registry value LastShortcutName is not a string, so it was not used: no current shortcut is taken as recorded."
            : "The registry value PendingShortcutNames is not a list of strings, so it was not used: no other shortcuts are taken as recorded.",
      }, warnings, StringComparer.Ordinal);
      using(RegistryKey untouched = Assert.IsType<RegistryKey>(hive.Root.OpenSubKey(Key)))
      {
         Assert.Equal(kind, untouched.GetValueKind(name));
      }
      record.WriteLastShortcutName("Globex Connector.lnk");
      record.WritePendingShortcutNames(["Old Brand.lnk"]);
      Assert.Equal("Globex Connector.lnk", record.ReadLastShortcutName());
      Assert.Equal(new[] { "Old Brand.lnk" }, record.ReadPendingShortcutNames(), StringComparer.Ordinal);
      _ = Assert.Single(warnings);
      using RegistryKey replaced = Assert.IsType<RegistryKey>(hive.Root.OpenSubKey(Key));
      Assert.Equal(last ? RegistryValueKind.String : RegistryValueKind.MultiString, replaced.GetValueKind(name));
   }



   /// <summary>
   /// An expandable string under the current shortcut's name is a string and is read exactly as stored: the name of an
   /// environment variable in it is not expanded, so what the record returns never depends on the environment of the
   /// process that reads it. Nothing is warned.
   /// </summary>
   [Fact]
   public void ExpandableStringIsReadAsStoredWithoutExpansion()
   {
      using var hive = new PrivateRegistryHive();
      using(RegistryKey key = hive.Root.CreateSubKey(Key))
      {
         key.SetValue("LastShortcutName", "%SystemRoot% Connector.lnk", RegistryValueKind.ExpandString);
      }
      List<string> warnings = [];
      Assert.Equal("%SystemRoot% Connector.lnk", new RegistryShortcutRecord(hive.Root, Key, warnings.Add).ReadLastShortcutName());
      Assert.Empty(warnings);
   }



   /// <summary>
   /// The record of the installed product is bound to this product's key in the machine hive, in the 64-bit view, with
   /// the value names 1.x and the installer know. Only the handle of the machine hive is opened to see this; no key
   /// under it is opened, read or written.
   /// </summary>
   [Fact]
   public void InstalledProductRecordIsTheProductKeyOfTheMachineHiveInThe64BitView()
   {
      var record = RegistryShortcutRecord.Machine(_ => { });
      using RegistryKey root = record.Root;
      Assert.Equal("HKEY_LOCAL_MACHINE", root.Name);
      Assert.Equal(RegistryView.Registry64, root.View);
      Assert.Equal(@"SOFTWARE\QRunIO\PrintAgent", record.KeyPath);
      Assert.Equal("LastShortcutName", RegistryShortcutRecord.LastName);
      Assert.Equal("PendingShortcutNames", RegistryShortcutRecord.PendingNames);
   }
}
