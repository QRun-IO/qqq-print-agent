/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A browser callback carries a one-time code, so the helper hands it only to the companion installed beside it, and only
/// when both programs sit under Program Files in folders nobody but trusted accounts can change. These tests run that
/// check in a temporary stand-in for Program Files with folder and file security described by a fake.
/// </summary>
public sealed class BrowserCallbackInstallationTests
{
   /// <summary>
   /// From the installed layout the companion next to the helper is returned, after the security of every level from each
   /// program up to Program Files itself has been read: the file, its folder, the product folder and Program Files.
   /// </summary>
   [Fact]
   public void InstalledHelperIsGivenTheCompanionBesideItAfterEveryLevelIsChecked()
   {
      using var setup = new Setup();
      Assert.Equal(setup.Companion, QqqBrowserCallback.InstalledCompanionPath(setup.Helper, setup.ProgramFiles, setup.Security));
      string product = Path.Combine(setup.ProgramFiles, "QQQ Print Agent");
      Assert.Equal(new[]
      {
         setup.Helper, Path.Combine(product, "Cli"), product, setup.ProgramFiles,
         setup.Companion, Path.Combine(product, "App"), product, setup.ProgramFiles,
      }, setup.Security.Reads, StringComparer.Ordinal);
   }



   /// <summary>A process whose own path cannot be determined is refused, as is a helper that does not run from the installed layout; nothing is read for either.</summary>
   [Fact]
   public void UnknownOrMisplacedHelperIsRefusedBeforeAnythingIsRead()
   {
      using var setup = new Setup();
      Assert.Equal("Cannot locate the installed callback helper.", Assert.Throws<InvalidOperationException>(() => QqqBrowserCallback.InstalledCompanionPath(null, setup.ProgramFiles, setup.Security)).Message);
      Assert.Equal("Browser callbacks require the installed CLI location.", Assert.Throws<UnauthorizedAccessException>(() => QqqBrowserCallback.InstalledCompanionPath(Path.Combine(setup.ProgramFiles, "QQQ Print Agent", "qqq-print-agent.exe"), setup.ProgramFiles, setup.Security)).Message);
      Assert.Empty(setup.Security.Reads);
   }



   /// <summary>A helper with the right layout that is not under Program Files is refused: the layout alone proves nothing about who could have put it there.</summary>
   [Fact]
   public void HelperOutsideProgramFilesIsRefused()
   {
      using var setup = new Setup();
      string elsewhere = Path.Combine(setup.Root, "Downloads", "QQQ Print Agent", "Cli", "qqq-print-agent.exe");
      _ = Assert.Throws<InvalidDataException>(() => QqqBrowserCallback.InstalledCompanionPath(elsewhere, setup.ProgramFiles, setup.Security));
      Assert.Empty(setup.Security.Reads);
   }



   /// <summary>
   /// The callback is not handed over when an account that is not trusted may change, or owns, the helper, the companion or
   /// any folder above them up to Program Files, or when the companion is not there at all.
   /// </summary>
   [Theory]
   [InlineData("helper-writable")]
   [InlineData("companion-owner")]
   [InlineData("application-folder-writable")]
   [InlineData("product-folder-writable")]
   [InlineData("program-files-writable")]
   [InlineData("companion-missing")]
   public void UntrustedLevelStopsTheHandOver(string problem)
   {
      using var setup = new Setup();
      string product = Path.Combine(setup.ProgramFiles, "QQQ Print Agent");
      DirectorySecurity writable = FakePathSecurity.Descriptor(alsoAllow: FakePathSecurity.Users, rights: FileSystemRights.Modify);
      if(problem is "helper-writable")
      { setup.Security.Descriptors[setup.Helper] = writable; }
      else if(problem is "companion-owner")
      { setup.Security.Descriptors[setup.Companion] = FakePathSecurity.Descriptor(owner: FakePathSecurity.Users); }
      else if(problem is "application-folder-writable")
      { setup.Security.Descriptors[Path.Combine(product, "App")] = writable; }
      else if(problem is "product-folder-writable")
      { setup.Security.Descriptors[product] = writable; }
      else if(problem is "program-files-writable")
      { setup.Security.Descriptors[setup.ProgramFiles] = writable; }
      else
      { File.Delete(setup.Companion); }
      Exception refused = Assert.ThrowsAny<Exception>(() => QqqBrowserCallback.InstalledCompanionPath(setup.Helper, setup.ProgramFiles, setup.Security));
      Assert.True(problem is "companion-missing" ? refused is FileNotFoundException : refused is UnauthorizedAccessException);
   }



   /// <summary>A temporary stand-in for Program Files holding the installed layout (a helper in Cli, a companion in App), with trusted security unless a test says otherwise.</summary>
   private sealed class Setup : IDisposable
   {
      /// <summary>The temporary root.</summary>
      internal string Root { get; } = Path.Combine(Path.GetTempPath(), "qqq-callback-install-" + Guid.NewGuid().ToString("N"));



      /// <summary>The stand-in for Program Files.</summary>
      internal string ProgramFiles { get; }



      /// <summary>The callback helper in its installed place.</summary>
      internal string Helper { get; }



      /// <summary>The companion in its installed place.</summary>
      internal string Companion { get; }



      /// <summary>Folder and file security as the test describes it.</summary>
      internal FakePathSecurity Security { get; } = new();



      /// <summary>Creates the two folders and the two program files.</summary>
      internal Setup()
      {
         ProgramFiles = Path.Combine(Root, "Program Files");
         Helper = Path.Combine(ProgramFiles, "QQQ Print Agent", "Cli", "qqq-print-agent.exe");
         Companion = Path.Combine(ProgramFiles, "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");
         foreach(string program in new[] { Helper, Companion })
         {
            Directory.CreateDirectory(Path.GetDirectoryName(program)!);
            File.WriteAllText(program, "synthetic program");
         }
      }



      /// <summary>Removes the temporary root.</summary>
      public void Dispose() => Directory.Delete(Root, recursive: true);
   }
}