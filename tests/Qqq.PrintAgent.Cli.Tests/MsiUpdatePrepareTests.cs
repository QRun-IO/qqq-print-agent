/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Preparing an update and handing it to the helper. The prepared helper later installs an MSI with administrator
/// rights, so the tests check each thing that decides which file that will be and which program will do it: who asks,
/// where the MSI is and who may change it, which release it is, which process asked, where the helper is copied from,
/// and that a request or helper changed after preparation is refused before anything is started. Everything runs in
/// temporary folders against fakes; no program is started.
/// </summary>
public sealed class MsiUpdatePrepareTests
{
   /// <summary>What someone who is not an administrator is told.</summary>
   private const string NotAdministrator = "Machine updates require an elevated administrator. No installer was started.";



   /// <summary>What a path that lets ordinary users write is refused with.</summary>
   private const string Writable = "An update path allows modification by an untrusted identity. Repair the MSI directory permissions.";



   /// <summary>
   /// A prepared update has a helper folder holding a copy of the whole installed command line folder, and a request
   /// that names the staged MSI, the release, the MSI's SHA-256 and the companion that asked by process id and start
   /// time. Nothing is started, and the companion's process is let go again.
   /// </summary>
   [Fact]
   public void PreparedUpdateHasACopiedHelperAndARequestPinningTheMsiAndTheCompanion()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.Prepare();
      Assert.Equal(setup.RequestPath, request);
      MsiUpdateInstaller.InstallRequest written = JsonSerializer.Deserialize<MsiUpdateInstaller.InstallRequest>(File.ReadAllText(request), PrintApiClient.JsonOptions)!;
      Assert.Equal(new MsiUpdateInstaller.InstallRequest(setup.Package, MsiUpdateSetup.Version, Convert.ToHexString(SHA256.HashData(setup.PackageBytes)), MsiUpdateSetup.CompanionId, MsiUpdateSetup.CompanionStarted), written);
      Assert.Equal(new[] { "qqq-print-agent.dll", "qqq-print-agent.exe", Path.Combine("runtimes", "win-x64", "native.dll") },
         Directory.EnumerateFiles(setup.HelperDirectory, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(setup.HelperDirectory, file)).Order(StringComparer.Ordinal).ToArray());
      Assert.Equal(File.ReadAllText(setup.InstalledTool), File.ReadAllText(setup.Helper));
      Assert.Equal(new[] { MsiUpdateSetup.CompanionId }, setup.Processes.Opened);
      Assert.Equal(0, setup.Processes.OpenHandles);
      Assert.Empty(setup.Processes.Started);
   }



   /// <summary>
   /// Before anything is copied, the owner and access list of the installed command line folder are read at every level
   /// up to Program Files, and while copying, those of every folder and file that is copied.
   /// </summary>
   [Fact]
   public void EveryInstalledFolderAndCopiedFileIsCheckedForWhoMayChangeIt()
   {
      using var setup = new MsiUpdateSetup();
      _ = setup.Prepare();
      string cli = setup.Places.InstalledCli;
      foreach(string path in new[] { setup.ProgramFiles, setup.Places.InstalledRoot, cli, setup.InstalledTool, Path.Combine(cli, "qqq-print-agent.dll"), Path.Combine(cli, "runtimes"), Path.Combine(cli, "runtimes", "win-x64"), Path.Combine(cli, "runtimes", "win-x64", "native.dll") })
      {
         Assert.Contains(path, setup.Security.Reads, StringComparer.Ordinal);
      }
      Assert.DoesNotContain(setup.Root, setup.Security.Reads, StringComparer.Ordinal);
   }



   /// <summary>Someone who is not an administrator cannot prepare an update, and nothing is created.</summary>
   [Fact]
   public void NonAdministratorCannotPrepare()
   {
      using var setup = new MsiUpdateSetup();
      setup.Security.IsAdministrator = false;
      Assert.Equal(NotAdministrator, Assert.Throws<UnauthorizedAccessException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
      Assert.Empty(setup.Processes.Opened);
   }



   /// <summary>An MSI that is not inside the updates folder is refused, wherever else it is.</summary>
   [Fact]
   public void MsiOutsideTheUpdatesFolderIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      string elsewhere = Path.Combine(setup.Root, "Downloads", "package.msi");
      Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
      File.WriteAllBytes(elsewhere, setup.PackageBytes);
      InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => setup.Steps.PrepareHelper(elsewhere, MsiUpdateSetup.Version, MsiUpdateSetup.CompanionId));
      Assert.Equal("Update paths must remain inside the protected staging directory.", refusal.Message);
      Assert.False(Directory.Exists(Path.Combine(setup.Root, "Downloads", "helper")));
   }



   /// <summary>A session folder that ordinary users may write to is refused, because the MSI could be swapped after it was checked.</summary>
   [Fact]
   public void SessionOrdinaryUsersMayWriteToIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      setup.Security.Descriptors[setup.Session] = MsiUpdateSetup.WritableByUsers();
      Assert.Equal(Writable, Assert.Throws<UnauthorizedAccessException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
   }



   /// <summary>An MSI that ordinary users may write to is refused.</summary>
   [Fact]
   public void MsiOrdinaryUsersMayWriteToIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      setup.Security.Descriptors[setup.Package] = MsiUpdateSetup.WritableByUsers();
      Assert.Equal(Writable, Assert.Throws<UnauthorizedAccessException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
   }



   /// <summary>A junction where the MSI should be is refused as a link, before it is looked at as a file.</summary>
   [Fact]
   public void JunctionWhereTheMsiShouldBeIsRefusedAsALink()
   {
      using var setup = new MsiUpdateSetup();
      string elsewhere = Path.Combine(setup.Root, "elsewhere");
      Directory.CreateDirectory(elsewhere);
      File.Delete(setup.Package);
      MsiUpdateSetup.MakeJunction(setup.Package, elsewhere);
      Assert.Equal("Update paths cannot contain symbolic links or junctions.", Assert.Throws<IOException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
   }



   /// <summary>An MSI that is missing is reported as a missing staged file.</summary>
   [Fact]
   public void MissingMsiIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      File.Delete(setup.Package);
      Assert.Equal("A required staged update file is missing.", Assert.Throws<FileNotFoundException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
   }



   /// <summary>
   /// An MSI that is empty, that is larger than 256 MiB or that does not end in .msi is refused. The large one is a
   /// file whose length is set without writing it, so the test costs no time.
   /// </summary>
   [Theory]
   [InlineData("package.msi", 0L)]
   [InlineData("package.msi", (256L * 1024 * 1024) + 1)]
   [InlineData("package.exe", 10L)]
   [InlineData("package", 10L)]
   public void MsiOfTheWrongSizeOrNameIsRefused(string name, long length)
   {
      using var setup = new MsiUpdateSetup();
      string package = Path.Combine(setup.Session, name);
      using(FileStream stream = new(package, FileMode.Create, FileAccess.Write, FileShare.None))
      { stream.SetLength(length); }
      InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => setup.Steps.PrepareHelper(package, MsiUpdateSetup.Version, MsiUpdateSetup.CompanionId));
      Assert.Equal("The staged MSI has an invalid size or extension.", refusal.Message);
      AssertNothingPrepared(setup);
      Assert.Empty(setup.Processes.Opened);
   }



   /// <summary>A release this updater does not support (older than 0.1.3, or no version at all) is refused before the companion is looked at.</summary>
   [Theory]
   [InlineData("0.1.2")]
   [InlineData("0.1.2-rc.9")]
   [InlineData("latest")]
   [InlineData("")]
   public void ReleaseThatIsNotEligibleIsRefused(string version)
   {
      using var setup = new MsiUpdateSetup();
      InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => setup.Steps.PrepareHelper(setup.Package, version, MsiUpdateSetup.CompanionId));
      Assert.Equal("The update version is not an eligible release.", refusal.Message);
      AssertNothingPrepared(setup);
      Assert.Empty(setup.Processes.Opened);
   }



   /// <summary>A process id that no running process has is refused with what Windows says about it.</summary>
   [Fact]
   public void CompanionThatIsNotRunningIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      _ = Assert.Throws<ArgumentException>(() => setup.Steps.PrepareHelper(setup.Package, MsiUpdateSetup.Version, 999_999));
      AssertNothingPrepared(setup);
   }



   /// <summary>A process whose program cannot be told is refused.</summary>
   [Fact]
   public void CompanionWithoutAProgramPathIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      setup.Companion.Program = null;
      Assert.Equal("Cannot verify the update's companion process.", Assert.Throws<InvalidOperationException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>Only the companion at its install path may prepare an update: a copy elsewhere, or another installed program, may not.</summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void ProcessThatIsNotTheInstalledCompanionIsRefused(bool anotherInstalledProgram)
   {
      using var setup = new MsiUpdateSetup();
      setup.Companion.Program = anotherInstalledProgram ? setup.InstalledTool : Path.Combine(setup.Root, "Downloads", "Qqq.PrintAgent.exe");
      Assert.Equal("Only the installed companion may prepare an update.", Assert.Throws<InvalidOperationException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>Windows reports a program path in whatever case it was started with, and may report it unnormalized; the installed companion is recognized either way.</summary>
   [Fact]
   public void InstalledCompanionIsRecognizedWhateverTheCaseOfItsPath()
   {
      using var setup = new MsiUpdateSetup();
      setup.Companion.Program = Path.Combine(setup.Places.InstalledRoot, "Cli", "..", "App", "QQQ.PRINTAGENT.EXE").ToUpperInvariant();
      Assert.Equal(setup.RequestPath, setup.Prepare());
   }



   /// <summary>The installed command line folder is refused when ordinary users may write to it or to a folder above it, up to and including Program Files.</summary>
   [Theory]
   [InlineData(0)]
   [InlineData(1)]
   [InlineData(2)]
   public void InstalledFolderOrdinaryUsersMayWriteToIsRefused(int level)
   {
      using var setup = new MsiUpdateSetup();
      setup.Security.Descriptors[new[] { setup.Places.InstalledCli, setup.Places.InstalledRoot, setup.ProgramFiles }[level]] = MsiUpdateSetup.WritableByUsers();
      Assert.Equal(Writable, Assert.Throws<UnauthorizedAccessException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
   }



   /// <summary>An installed command line folder that is really a junction to somewhere else is refused.</summary>
   [Fact]
   public void InstalledFolderThatIsAJunctionIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      string real = Path.Combine(setup.Root, "elsewhere");
      Directory.Move(setup.Places.InstalledCli, real);
      MsiUpdateSetup.MakeJunction(setup.Places.InstalledCli, real);
      Assert.Equal("Update paths cannot contain symbolic links or junctions.", Assert.Throws<IOException>(() => setup.Prepare()).Message);
      AssertNothingPrepared(setup);
   }



   /// <summary>A helper folder left by an earlier preparation in the same session is never reused or overwritten.</summary>
   [Fact]
   public void HelperFolderThatAlreadyExistsIsNotReused()
   {
      using var setup = new MsiUpdateSetup();
      Directory.CreateDirectory(setup.HelperDirectory);
      File.WriteAllText(setup.Helper, "left by someone else");
      Assert.Equal("This update helper directory already exists. Check again to stage a fresh update.", Assert.Throws<IOException>(() => setup.Prepare()).Message);
      Assert.Equal("left by someone else", File.ReadAllText(setup.Helper));
      Assert.False(File.Exists(setup.RequestPath));
   }



   /// <summary>A junction inside the installed command line folder stops the copy, so nothing is copied from where it points, and no request is written.</summary>
   [Fact]
   public void JunctionInsideTheInstalledFolderStopsTheCopy()
   {
      using var setup = new MsiUpdateSetup();
      string elsewhere = Path.Combine(setup.Root, "elsewhere");
      Directory.CreateDirectory(elsewhere);
      File.WriteAllText(Path.Combine(elsewhere, "planted.dll"), "synthetic planted program");
      MsiUpdateSetup.MakeJunction(Path.Combine(setup.Places.InstalledCli, "linked"), elsewhere);
      Assert.Equal("Update helper source cannot contain symbolic links or junctions.", Assert.Throws<IOException>(() => setup.Prepare()).Message);
      Assert.Empty(Directory.EnumerateFiles(setup.HelperDirectory, "planted.dll", SearchOption.AllDirectories));
      Assert.False(File.Exists(setup.RequestPath));
   }



   /// <summary>A file or a folder inside the installed command line folder that ordinary users may write to stops the copy before that file is copied, and no request is written.</summary>
   [Theory]
   [InlineData("qqq-print-agent.dll", "qqq-print-agent.dll")]
   [InlineData("runtimes", "runtimes/win-x64/native.dll")]
   [InlineData("runtimes/win-x64/native.dll", "runtimes/win-x64/native.dll")]
   public void WritableFileOrFolderInsideTheInstalledFolderStopsTheCopy(string writable, string notCopied)
   {
      using var setup = new MsiUpdateSetup();
      setup.Security.Descriptors[Path.GetFullPath(Path.Combine(setup.Places.InstalledCli, writable))] = MsiUpdateSetup.WritableByUsers();
      Assert.Equal(Writable, Assert.Throws<UnauthorizedAccessException>(() => setup.Prepare()).Message);
      Assert.False(File.Exists(Path.Combine(setup.HelperDirectory, notCopied)));
      Assert.False(File.Exists(setup.RequestPath));
   }



   /// <summary>An installation without its command line tool cannot be updated from the app, and no request is written.</summary>
   [Fact]
   public void InstallationWithoutTheCommandLineToolIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      File.Delete(setup.InstalledTool);
      Assert.Equal("The installed command line tool is missing. Repair the machine MSI first.", Assert.Throws<FileNotFoundException>(() => setup.Prepare()).Message);
      Assert.False(File.Exists(setup.RequestPath));
   }



   /// <summary>
   /// The hand-off starts the copied helper, and nothing else, without a shell and without a window, in its own folder,
   /// with exactly two arguments: the apply switch and the request's full path. The started process is let go.
   /// </summary>
   [Fact]
   public void HandOffStartsTheCopiedHelperWithTheRequest()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.Prepare();
      setup.Steps.StartHelper(request);
      System.Diagnostics.ProcessStartInfo start = Assert.Single(setup.Processes.Started);
      Assert.Equal(setup.Helper, start.FileName);
      Assert.Equal(new[] { "--apply-update", request }, start.ArgumentList, StringComparer.Ordinal);
      Assert.Equal((false, true, setup.HelperDirectory), (start.UseShellExecute, start.CreateNoWindow, start.WorkingDirectory));
      Assert.Equal("", start.Arguments);
      Assert.Equal(0, setup.Processes.OpenHandles);
   }



   /// <summary>Someone who is not an administrator cannot hand off, and nothing is started.</summary>
   [Fact]
   public void NonAdministratorCannotHandOff()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.Prepare();
      setup.Security.IsAdministrator = false;
      Assert.Equal(NotAdministrator, Assert.Throws<UnauthorizedAccessException>(() => setup.Steps.StartHelper(request)).Message);
      Assert.Empty(setup.Processes.Started);
   }



   /// <summary>
   /// A request that is not the file the preparation wrote is refused and nothing is started: one under another name,
   /// one larger than 16 KiB, one that holds no request, and one whose fields are not acceptable (an MSI outside the
   /// session, a hash that is too short or not hexadecimal, a companion without a start time, an older release).
   /// </summary>
   [Theory]
   [InlineData("renamed", "Invalid update request file.")]
   [InlineData("oversize", "Invalid update request file.")]
   [InlineData("null", "Empty update request.")]
   [InlineData("package-elsewhere", "Update paths must remain inside the protected staging directory.")]
   [InlineData("bad-hash", "Invalid update request fields.")]
   [InlineData("hash-not-hexadecimal", "Invalid update request fields.")]
   [InlineData("no-start-time", "Invalid update request fields.")]
   [InlineData("old-release", "The update version is not an eligible release.")]
   public void RequestThatWasChangedIsRefused(string change, string expected)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.Prepare();
      MsiUpdateInstaller.InstallRequest written = JsonSerializer.Deserialize<MsiUpdateInstaller.InstallRequest>(File.ReadAllText(request), PrintApiClient.JsonOptions)!;
      switch(change)
      {
         case "renamed":
            string renamed = Path.Combine(setup.Session, "request-2.json");
            File.Move(request, renamed);
            request = renamed;
            break;
         case "oversize":
            File.WriteAllText(request, File.ReadAllText(request) + new string(' ', 16 * 1024));
            break;
         case "null":
            File.WriteAllText(request, "null");
            break;
         case "package-elsewhere":
            File.WriteAllText(request, JsonSerializer.Serialize(written with { PackagePath = Path.Combine(setup.Root, "Downloads", "package.msi") }, PrintApiClient.JsonOptions));
            break;
         case "bad-hash":
            File.WriteAllText(request, JsonSerializer.Serialize(written with { Sha256 = "not-a-hash" }, PrintApiClient.JsonOptions));
            break;
         case "hash-not-hexadecimal":
            File.WriteAllText(request, JsonSerializer.Serialize(written with { Sha256 = new string('G', 64) }, PrintApiClient.JsonOptions));
            break;
         case "no-start-time":
            File.WriteAllText(request, JsonSerializer.Serialize(written with { ParentStartTicks = 0 }, PrintApiClient.JsonOptions));
            break;
         default:
            File.WriteAllText(request, JsonSerializer.Serialize(written with { Version = "0.1.2" }, PrintApiClient.JsonOptions));
            break;
      }
      InvalidDataException refusal = Assert.Throws<InvalidDataException>(() => setup.Steps.StartHelper(request));
      Assert.Equal(expected, refusal.Message);
      Assert.Empty(setup.Processes.Started);
   }



   /// <summary>
   /// A request, an MSI or a helper that ordinary users may write to after preparation is refused and nothing is
   /// started, and so is a session whose MSI or helper has gone missing.
   /// </summary>
   [Theory]
   [InlineData("request.json", false)]
   [InlineData("package", false)]
   [InlineData("helper/qqq-print-agent.exe", false)]
   [InlineData("request.json", true)]
   [InlineData("package", true)]
   [InlineData("helper/qqq-print-agent.exe", true)]
   public void StagedFileThatBecameWritableOrWentMissingIsRefused(string which, bool missing)
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.Prepare();
      string file = string.Equals(which, "package", StringComparison.Ordinal) ? setup.Package : Path.GetFullPath(Path.Combine(setup.Session, which));
      if(missing)
      {
         File.Delete(file);
         Assert.Equal("A required staged update file is missing.", Assert.Throws<FileNotFoundException>(() => setup.Steps.StartHelper(request)).Message);
      }
      else
      {
         setup.Security.Descriptors[file] = MsiUpdateSetup.WritableByUsers();
         Assert.Equal(Writable, Assert.Throws<UnauthorizedAccessException>(() => setup.Steps.StartHelper(request)).Message);
      }
      Assert.Empty(setup.Processes.Started);
   }



   /// <summary>An MSI emptied after preparation is refused at the hand-off, and nothing is started.</summary>
   [Fact]
   public void MsiEmptiedAfterPreparationIsRefused()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.Prepare();
      File.WriteAllBytes(setup.Package, []);
      Assert.Equal("The staged MSI has an invalid size or extension.", Assert.Throws<InvalidDataException>(() => setup.Steps.StartHelper(request)).Message);
      Assert.Empty(setup.Processes.Started);
   }



   /// <summary>When Windows starts nothing for the helper, the hand-off fails and says so.</summary>
   [Fact]
   public void HelperThatDoesNotStartFailsTheHandOff()
   {
      using var setup = new MsiUpdateSetup();
      string request = setup.Prepare();
      setup.Processes.OnStart = _ => null;
      Assert.Equal("Could not start the staged update helper.", Assert.Throws<InvalidOperationException>(() => setup.Steps.StartHelper(request)).Message);
   }



   /// <summary>Nothing of a preparation exists: no helper folder and no request.</summary>
   private static void AssertNothingPrepared(MsiUpdateSetup setup)
   {
      Assert.False(Directory.Exists(setup.HelperDirectory));
      Assert.False(File.Exists(setup.RequestPath));
   }
}
