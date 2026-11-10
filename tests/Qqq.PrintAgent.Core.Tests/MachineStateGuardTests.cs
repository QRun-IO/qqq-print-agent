/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>
/// The rules that decide whether the machine state folder may be trusted, run against a per-test temporary folder with
/// chosen owners, access rules and links. Nothing here reads or writes the real ProgramData folder, and no test needs
/// to be elevated: the Windows facts come from a hand-written fake.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MachineStateGuardTests
{
   /// <summary>A path outside the guarded folder, including a sibling whose name merely starts the same way, is none of the guard's business: nothing is checked and nothing is created.</summary>
   [Fact]
   public void PathsOutsideTheGuardedFolderAreLeftAlone()
   {
      using var temp = new TempDirectory();
      var platform = new FakeMachineStatePlatform { IsAdministrator = true };
      var guard = new MachineStateGuard(temp.File("QqqPrintAgent"), platform);
      guard.EnsureForPath(temp.File("diagnostics"));
      guard.EnsureForPath(temp.File("QqqPrintAgentCopy\\agent.db"));
      guard.ValidateExistingForPath(temp.File("QqqPrintAgent\\branding.json"));
      Assert.Empty(platform.Created);
      Assert.Equal(0, platform.SecurityReads);
      Assert.False(Directory.Exists(guard.Root));
   }



   /// <summary>A missing machine folder is created, with the protected descriptor, only by an administrator; anyone else is refused and nothing is created. Once it passed, it is not validated again.</summary>
   [Fact]
   public void MissingFolderIsCreatedProtectedOnlyByAnAdministrator()
   {
      using var temp = new TempDirectory();
      var platform = new FakeMachineStatePlatform();
      var guard = new MachineStateGuard(temp.File("QqqPrintAgent"), platform);
      Assert.Throws<UnauthorizedAccessException>(() => guard.EnsureForPath(temp.File("QqqPrintAgent\\agent.db")));
      Assert.Empty(platform.Created);

      platform.IsAdministrator = true;
      guard.EnsureForPath(temp.File("qqqprintagent\\AGENT.DB"));
      Assert.Equal((guard.Root, "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"), Assert.Single(platform.Created));
      Assert.Equal(MachineStateGuard.ProtectedDescriptor, platform.Created[0].Descriptor);
      Assert.Equal(1, platform.SecurityReads);

      guard.EnsureForPath(guard.Root);
      Assert.Equal(1, platform.SecurityReads);
   }



   /// <summary>An existing folder is accepted when it and everything inside it is owned by a trusted account and open to nobody else; every folder and file is inspected.</summary>
   [Fact]
   public void ExistingTrustedTreeIsAcceptedAfterInspectingEveryEntry()
   {
      using var temp = new TempDirectory();
      string root = Populate(temp);
      var platform = new FakeMachineStatePlatform();
      var guard = new MachineStateGuard(root, platform);
      guard.ValidateExistingForPath(Path.Combine(root, "staging", "job.document"));
      Assert.Equal(4, platform.SecurityReads);
      Assert.Empty(platform.Created);
   }



   /// <summary>One entry anywhere in the tree that an ordinary user owns or may read makes the whole folder untrusted, and it stays untrusted on the next attempt.</summary>
   [Theory]
   [InlineData("", FakeMachineStatePlatform.UserOwned)]
   [InlineData("agent.db", FakeMachineStatePlatform.UserReadable)]
   [InlineData("staging", FakeMachineStatePlatform.UserReadable)]
   [InlineData("staging\\job.document", FakeMachineStatePlatform.UserOwned)]
   [InlineData("staging\\job.document", FakeMachineStatePlatform.NoAccessList)]
   public void UntrustedEntryAnywhereMakesTheFolderUntrusted(string entry, string descriptor)
   {
      using var temp = new TempDirectory();
      string root = Populate(temp);
      var platform = new FakeMachineStatePlatform { IsAdministrator = true };
      platform.Descriptors[Path.Combine(root, entry).TrimEnd('\\')] = descriptor;
      var guard = new MachineStateGuard(root, platform);
      Assert.Throws<UnauthorizedAccessException>(() => guard.EnsureForPath(root));
      Assert.Throws<UnauthorizedAccessException>(() => guard.EnsureForPath(root));
      Assert.Throws<UnauthorizedAccessException>(() => guard.ValidateTree(root));
   }



   /// <summary>A link or junction is refused wherever it sits: the folder itself, a folder above it, or a file or folder inside it, because it could redirect privileged code somewhere else.</summary>
   [Theory]
   [InlineData("root")]
   [InlineData("parent")]
   [InlineData("agent.db")]
   [InlineData("staging")]
   public void LinkAnywhereOnOrUnderThePathIsRefused(string link)
   {
      using var temp = new TempDirectory();
      string root = Populate(temp);
      var platform = new FakeMachineStatePlatform();
      platform.Links.Add(link switch
      {
         "root" => root,
         "parent" => temp.Path,
         _ => Path.Combine(root, link),
      });
      var guard = new MachineStateGuard(root, platform);
      string staged = string.Equals(link, "staging", StringComparison.Ordinal) ? Path.Combine(root, "staging", "job.document") : Path.Combine(root, "agent.db");
      Assert.Throws<UnauthorizedAccessException>(() => guard.EnsureForPath(Path.Combine(root, "agent.db")));
      Assert.Throws<UnauthorizedAccessException>(() => guard.ValidateFile(staged));
   }



   /// <summary>A file squatting on the folder's name is not adopted: without administrator rights the guard refuses, and it never treats the file as the folder.</summary>
   [Fact]
   public void FileInPlaceOfTheFolderIsRefused()
   {
      using var temp = new TempDirectory();
      File.WriteAllText(temp.File("QqqPrintAgent"), "not a folder");
      var platform = new FakeMachineStatePlatform();
      var guard = new MachineStateGuard(temp.File("QqqPrintAgent"), platform);
      Assert.Throws<UnauthorizedAccessException>(() => guard.ValidateExistingForPath(temp.File("QqqPrintAgent\\branding.json")));
      Assert.Empty(platform.Created);
   }



   /// <summary>A newly staged file is accepted only when both it and the folder holding it are trusted.</summary>
   [Theory]
   [InlineData(null, true)]
   [InlineData("staging\\job.document", false)]
   [InlineData("staging", false)]
   public void StagedFileIsAcceptedOnlyWithATrustedFolder(string? untrusted, bool accepted)
   {
      using var temp = new TempDirectory();
      string root = Populate(temp);
      var platform = new FakeMachineStatePlatform();
      if(untrusted is not null)
      { platform.Descriptors[Path.Combine(root, untrusted)] = FakeMachineStatePlatform.UserReadable; }
      var guard = new MachineStateGuard(root, platform);
      string file = Path.Combine(root, "staging", "job.document");
      if(accepted)
      {
         guard.ValidateFile(file);
         Assert.Equal(2, platform.SecurityReads);
      }
      else
      { Assert.Throws<UnauthorizedAccessException>(() => guard.ValidateFile(file)); }
   }



   /// <summary>
   /// The fixed entry point guards the folder under ProgramData and nothing else: a temporary path is left alone, and a
   /// staged file that ordinary users may read is refused through it exactly as through the guard.
   /// </summary>
   [Fact]
   public void FixedEntryPointGuardsOnlyTheMachineFolder()
   {
      using var temp = new TempDirectory();
      string file = temp.File("job.document");
      File.WriteAllText(file, "synthetic");
      var info = new FileInfo(file);
      FileSecurity security = info.GetAccessControl();
      security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.Read, AccessControlType.Allow));
      info.SetAccessControl(security);

      Assert.EndsWith("\\QqqPrintAgent", MachineStateDirectory.Root, StringComparison.Ordinal);
      MachineStateDirectory.EnsureForPath(file);
      MachineStateDirectory.ValidateExistingForPath(file);
      Assert.Throws<UnauthorizedAccessException>(() => MachineStateDirectory.ValidateFile(file));
   }



   /// <summary>Creates the guarded folder with a database file and a staging folder holding one staged document, and returns its path.</summary>
   private static string Populate(TempDirectory temp)
   {
      string root = temp.File("QqqPrintAgent");
      Directory.CreateDirectory(Path.Combine(root, "staging"));
      File.WriteAllText(Path.Combine(root, "agent.db"), "synthetic");
      File.WriteAllText(Path.Combine(root, "staging", "job.document"), "synthetic");
      return root;
   }



   /// <summary>
   /// Windows facts chosen by the test: every path is trusted and no path is a link unless the test says otherwise.
   /// Attributes other than the link flag, and the folder that gets created, are real, inside the test's temporary folder.
   /// </summary>
   private sealed class FakeMachineStatePlatform : IMachineStatePlatform
   {
      internal const string Trusted = "O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
      internal const string UserOwned = "O:S-1-5-21-100-200-300-1001G:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";
      internal const string UserReadable = "O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;GR;;;BU)";
      internal const string NoAccessList = "O:SYG:SYD:NO_ACCESS_CONTROL";



      /// <summary>The descriptor reported for a path; any path not listed is trusted.</summary>
      internal Dictionary<string, string> Descriptors { get; } = new(StringComparer.OrdinalIgnoreCase);



      /// <summary>Paths reported as links.</summary>
      internal HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);



      /// <summary>Every folder the guard asked to have created, with the descriptor it asked for.</summary>
      internal List<(string Path, string Descriptor)> Created { get; } = [];



      /// <summary>How many security descriptors the guard read.</summary>
      internal int SecurityReads { get; private set; }



      /// <inheritdoc/>
      public bool IsAdministrator { get; set; }



      /// <summary>The real attributes, with the link flag added for paths the test declared to be links.</summary>
      public FileAttributes GetAttributes(string path) => File.GetAttributes(path) | (Links.Contains(path) ? FileAttributes.ReparsePoint : FileAttributes.None);



      /// <summary>A folder descriptor built from the SDDL chosen for the path.</summary>
      public FileSystemSecurity GetDirectorySecurity(string path)
      {
         SecurityReads++;
         DirectorySecurity security = new();
         security.SetSecurityDescriptorSddlForm(Descriptors.GetValueOrDefault(path, Trusted));
         return security;
      }



      /// <summary>A file descriptor built from the SDDL chosen for the path.</summary>
      public FileSystemSecurity GetFileSecurity(string path)
      {
         SecurityReads++;
         FileSecurity security = new();
         security.SetSecurityDescriptorSddlForm(Descriptors.GetValueOrDefault(path, Trusted));
         return security;
      }



      /// <summary>Records the request and creates an ordinary folder in the test's temporary folder.</summary>
      public void CreateProtectedDirectory(string path, string descriptor)
      {
         Created.Add((path, descriptor));
         Directory.CreateDirectory(path);
      }
   }
}
