/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Xml.Linq;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The decisions about the sign-in startup task, run against a task scheduler that exists only in memory: who may change
/// the task, when it is created, replaced, removed or left alone, what is registered, and what is reported when Windows
/// refuses, hangs or stores something else. No test here reads or changes this computer's scheduled tasks.
/// </summary>
public sealed class AgentStartupTasksTests
{
   private const string Sid = FakeTaskScheduler.UserSid;
   private const string TaskName = "QQQ Print Agent " + Sid;
   private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";



   /// <summary>Someone who is not an administrator is told so and nothing is asked of the scheduler, for the status as for a change or the uninstall cleanup.</summary>
   [Fact]
   public async Task NonAdministratorIsRefusedBeforeTheSchedulerIsAsked()
   {
      using var setup = new Setup();
      setup.Scheduler.Caller = setup.Scheduler.Caller with { IsAdministrator = false };
      Assert.Equal(new AgentStartupStatus(false, false, "Administrator authorization is required to configure sign-in startup."), await setup.Rules.GetStatusAsync());
      UnauthorizedAccessException change = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Rules.SetEnabledAsync(true));
      Assert.Equal("Configure sign-in startup from the elevated installed companion.", change.Message);
      UnauthorizedAccessException cleanup = await Assert.ThrowsAsync<UnauthorizedAccessException>(setup.Rules.RemoveInstalledTasksAsync);
      Assert.Equal("Removing machine installation startup tasks requires administrator authorization.", cleanup.Message);
      Assert.Empty(setup.Scheduler.Runs);
   }



   /// <summary>An identity without a security identifier cannot name a task, so the status and a change fail with that reason and nothing is asked of the scheduler.</summary>
   [Fact]
   public async Task CallerWithoutASecurityIdentifierCannotNameATask()
   {
      using var setup = new Setup();
      setup.Scheduler.Caller = setup.Scheduler.Caller with { UserSid = null };
      Assert.Equal("Current user has no SID.", (await Assert.ThrowsAsync<InvalidOperationException>(setup.Rules.GetStatusAsync)).Message);
      Assert.Equal("Current user has no SID.", (await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Rules.SetEnabledAsync(true))).Message);
      Assert.Empty(setup.Scheduler.Runs);
   }



   /// <summary>With no task registered, the installed companion is offered the preference and a development or portable copy is told why it is not; the one query sent names this user's task.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task WithoutATaskThePreferenceIsOfferedOnlyToTheInstalledCompanion(bool installed)
   {
      using var setup = new Setup();
      if(!installed)
      { setup.Scheduler.Caller = setup.Scheduler.Caller with { ProcessPath = Path.Combine(setup.Root, "bin", "Qqq.PrintAgent.exe") }; }
      AgentStartupStatus status = await setup.Rules.GetStatusAsync();
      Assert.Equal(installed
         ? new AgentStartupStatus(false, true, "Starts the installed companion in your tray at your next sign-in. The service already starts with Windows.")
         : new AgentStartupStatus(false, false, "Enable this from the MSI-installed companion. Development and portable executables cannot create an elevated sign-in task."), status);
      Assert.Equal(new[] { "/Query", "/TN", TaskName, "/XML" }, Assert.Single(setup.Scheduler.Runs));
      Assert.Equal(TimeSpan.FromSeconds(15), setup.Scheduler.LastLimit);
   }



   /// <summary>A registered task of ours is reported enabled unless its settings say it is disabled, and an enabled task keeps the preference available even to a copy that could not create it, so it can still be turned off.</summary>
   [Fact]
   public async Task OwnTaskIsReportedEnabledOrDisabledAsItsSettingsSay()
   {
      using var setup = new Setup();
      setup.Scheduler.Tasks[TaskName] = setup.Rules.CreateTaskXml(Sid);
      Assert.True((await setup.Rules.GetStatusAsync()).Enabled);
      setup.Scheduler.Caller = setup.Scheduler.Caller with { ProcessPath = Path.Combine(setup.Root, "bin", "Qqq.PrintAgent.exe") };
      AgentStartupStatus fromACopy = await setup.Rules.GetStatusAsync();
      Assert.True(fromACopy.Enabled);
      Assert.True(fromACopy.Available);
      var disabled = XDocument.Parse(setup.Rules.CreateTaskXml(Sid));
      disabled.Root!.Element(Ns + "Settings")!.Element(Ns + "Enabled")!.Value = "False";
      setup.Scheduler.Tasks[TaskName] = disabled.ToString();
      AgentStartupStatus off = await setup.Rules.GetStatusAsync();
      Assert.False(off.Enabled);
      Assert.False(off.Available);
   }



   /// <summary>A task under this user's task name that is not exactly ours is reported as someone else's, never as enabled, and neither enabling nor disabling touches it.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task ForeignTaskUnderOurNameIsReportedAndLeftUnchanged(bool enable)
   {
      using var setup = new Setup();
      string foreign = setup.Rules.CreateTaskXml(Sid).Replace("--tray", "--something-else", StringComparison.Ordinal);
      setup.Scheduler.Tasks[TaskName] = foreign;
      Assert.Equal(new AgentStartupStatus(false, false, "A different task definition uses this startup name. Inspect it in Windows Task Scheduler before changing this preference."), await setup.Rules.GetStatusAsync());
      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Rules.SetEnabledAsync(enable));
      Assert.Equal("A different task definition uses this startup name. It was left unchanged; inspect it in Windows Task Scheduler.", refused.Message);
      Assert.All(setup.Scheduler.Runs, run => Assert.Equal("/Query", run[0]));
      Assert.Equal(foreign, setup.Scheduler.Tasks[TaskName]);
      Assert.False(Directory.Exists(setup.SetupDirectory));
   }



   /// <summary>
   /// Enabling from the installed companion registers exactly the generated definition for this user: it is staged in the
   /// setup folder as UTF-16 with a byte-order mark, held so it cannot be swapped while the scheduler reads it, registered
   /// with replacement allowed, read back and checked, and the staged file is gone afterwards. The commands are query,
   /// create, query to verify, query for the status.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public async Task EnablingRegistersTheGeneratedDefinitionAndVerifiesIt(bool replacesOurEarlierTask)
   {
      using var setup = new Setup();
      if(replacesOurEarlierTask)
      { setup.Scheduler.Tasks[TaskName] = setup.Rules.CreateTaskXml(Sid).Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>", StringComparison.Ordinal); }
      AgentStartupStatus status = await setup.Rules.SetEnabledAsync(true);
      Assert.Equal(new AgentStartupStatus(true, true, "Starts the installed companion in your tray at your next sign-in. The service already starts with Windows."), status);
      Assert.Equal(new[] { "/Query", "/Create", "/Query", "/Query" }, setup.Scheduler.Runs.Select(run => run[0]), StringComparer.Ordinal);
      (string path, string text, byte[] start) = Assert.NotNull(setup.Scheduler.Definition);
      Assert.Equal(new[] { "/Create", "/TN", TaskName, "/XML", path, "/F" }, setup.Scheduler.Runs[1]);
      Assert.Equal(setup.SetupDirectory, Path.GetDirectoryName(path));
      Assert.Matches("^startup-[0-9a-f]{32}\\.xml$", Path.GetFileName(path));
      Assert.Equal(setup.Rules.CreateTaskXml(Sid), text);
      Assert.Equal(new byte[] { 0xFF, 0xFE }, start);
      Assert.True(setup.Scheduler.DefinitionWasHeld);
      Assert.Equal(new[] { setup.SetupDirectory, path }, setup.EnsuredFolders, StringComparer.Ordinal);
      Assert.Equal(new[] { path }, setup.ValidatedFiles, StringComparer.Ordinal);
      Assert.Equal(setup.Rules.CreateTaskXml(Sid), setup.Scheduler.Tasks[TaskName]);
      Assert.Empty(Directory.GetFiles(setup.SetupDirectory));
   }



   /// <summary>A development or portable copy cannot enable the elevated task: it is refused after the one query, and nothing is staged or registered.</summary>
   [Fact]
   public async Task EnablingIsRefusedForACopyThatIsNotTheInstalledCompanion()
   {
      using var setup = new Setup();
      setup.Scheduler.Caller = setup.Scheduler.Caller with { ProcessPath = Path.Combine(setup.Root, "bin", "Qqq.PrintAgent.exe") };
      InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Rules.SetEnabledAsync(true));
      Assert.Equal("Only the installed Program Files companion can enable elevated sign-in startup.", refused.Message);
      Assert.Equal("/Query", Assert.Single(setup.Scheduler.Runs)[0]);
      Assert.Empty(setup.Scheduler.Tasks);
      Assert.False(Directory.Exists(setup.SetupDirectory));
   }



   /// <summary>
   /// The definition is registered only from a place that passed both checks. When the setup folder cannot be trusted
   /// nothing is written; when the staged file does not have a trusted owner and access list it is not handed to the
   /// scheduler and is removed. In both cases the refusal reaches the caller and no task is registered.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task UntrustedStagingStopsRegistrationBeforeTheSchedulerSeesTheDefinition(bool folderUntrusted)
   {
      using var setup = new Setup();
      if(folderUntrusted)
      { setup.FolderRefusal = new UnauthorizedAccessException("Synthetic untrusted folder."); }
      else
      { setup.FileRefusal = new UnauthorizedAccessException("Synthetic untrusted file."); }
      UnauthorizedAccessException refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => setup.Rules.SetEnabledAsync(true));
      Assert.StartsWith("Synthetic untrusted", refused.Message, StringComparison.Ordinal);
      Assert.Equal("/Query", Assert.Single(setup.Scheduler.Runs)[0]);
      Assert.Empty(setup.Scheduler.Tasks);
      if(folderUntrusted)
      {
         Assert.False(Directory.Exists(setup.SetupDirectory));
      }
      else
      {
         Assert.Equal(setup.SetupDirectory, Path.GetDirectoryName(Assert.Single(setup.ValidatedFiles)));
         Assert.Empty(Directory.GetFiles(setup.SetupDirectory));
      }
   }



   /// <summary>When Windows refuses to register the task, that is reported, nothing is read back, and the staged definition is removed.</summary>
   [Fact]
   public async Task RefusedRegistrationIsReportedAndTheStagedDefinitionRemoved()
   {
      using var setup = new Setup();
      setup.Scheduler.Refuses["/Create"] = 1;
      InvalidOperationException failed = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Rules.SetEnabledAsync(true));
      Assert.Equal("Windows could not register the sign-in task. Check Task Scheduler and administrator permissions.", failed.Message);
      Assert.Equal(new[] { "/Query", "/Create" }, setup.Scheduler.Runs.Select(run => run[0]), StringComparer.Ordinal);
      Assert.Empty(Directory.GetFiles(setup.SetupDirectory));
   }



   /// <summary>
   /// A task that Windows stored differently from the definition it was given, or that cannot be read back at all, is not
   /// trusted: enabling fails with the verification message, the unexpected task is left as Windows stored it, and the
   /// staged definition is removed.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task RegisteredTaskThatDoesNotReadBackAsOursIsReported(bool storedDifferently)
   {
      using var setup = new Setup();
      if(storedDifferently)
      { setup.Scheduler.StoreAs = definition => definition.Replace("HighestAvailable", "LeastPrivilege", StringComparison.Ordinal); }
      else
      { setup.Scheduler.Unreadable.Add(TaskName); }
      InvalidOperationException failed = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Rules.SetEnabledAsync(true));
      Assert.Equal("The registered startup task could not be verified. Inspect Windows Task Scheduler; an unexpected task was left unchanged.", failed.Message);
      Assert.Equal(new[] { "/Query", "/Create", "/Query" }, setup.Scheduler.Runs.Select(run => run[0]), StringComparer.Ordinal);
      Assert.True(setup.Scheduler.Tasks.ContainsKey(TaskName));
      Assert.Empty(Directory.GetFiles(setup.SetupDirectory));
   }



   /// <summary>Disabling deletes our task with one forced delete of exactly its name and reports the preference off; with no task there is nothing to delete and no delete is sent.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task DisablingDeletesOnlyOurOwnTask(bool registered)
   {
      using var setup = new Setup();
      if(registered)
      { setup.Scheduler.Tasks[TaskName] = setup.Rules.CreateTaskXml(Sid); }
      AgentStartupStatus status = await setup.Rules.SetEnabledAsync(false);
      Assert.False(status.Enabled);
      Assert.Empty(setup.Scheduler.Tasks);
      string[][] deletes = setup.Scheduler.Runs.Where(run => run[0] is "/Delete").ToArray();
      if(registered)
      { Assert.Equal(new[] { "/Delete", "/TN", TaskName, "/F" }, Assert.Single(deletes)); }
      else
      { Assert.Empty(deletes); }
      Assert.DoesNotContain(setup.Scheduler.Runs, run => run[0] is "/Create");
   }



   /// <summary>When Windows refuses to delete the task, disabling says so instead of reporting the preference off.</summary>
   [Fact]
   public async Task RefusedDeletionIsReported()
   {
      using var setup = new Setup();
      setup.Scheduler.Tasks[TaskName] = setup.Rules.CreateTaskXml(Sid);
      setup.Scheduler.Refuses["/Delete"] = 5;
      InvalidOperationException failed = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Rules.SetEnabledAsync(false));
      Assert.Equal("Windows could not remove the sign-in task. Open Task Scheduler to inspect it.", failed.Message);
      Assert.True(setup.Scheduler.Tasks.ContainsKey(TaskName));
   }



   /// <summary>A scheduler that cannot be started, or that hangs and is stopped at the 15 second limit, is reported as such; the timeout carries the cancellation that ended the wait.</summary>
   [Fact]
   public async Task SchedulerThatCannotStartOrHangsIsReported()
   {
      using var setup = new Setup();
      setup.Scheduler.CannotStart = true;
      Assert.Equal("Could not start Windows Task Scheduler administration.", (await Assert.ThrowsAsync<InvalidOperationException>(setup.Rules.GetStatusAsync)).Message);
      setup.Scheduler.CannotStart = false;
      setup.Scheduler.HangsOn = "/Query";
      TimeoutException hung = await Assert.ThrowsAsync<TimeoutException>(() => setup.Rules.SetEnabledAsync(true));
      Assert.Equal("Windows Task Scheduler did not respond within 15 seconds. Refresh the preference to check its current state.", hung.Message);
      _ = Assert.IsType<OperationCanceledException>(hung.InnerException);
      Assert.Equal(TimeSpan.FromSeconds(15), setup.Scheduler.LastLimit);
   }



   /// <summary>
   /// Uninstall removes every task that is exactly ours, for any user, and nothing else. Left alone: a task that only
   /// starts with our prefix, a line whose name is not closed by a quote, a task under one of our names with someone
   /// else's definition, one whose definition cannot be read, and unrelated tasks.
   /// </summary>
   [Fact]
   public async Task UninstallRemovesExactlyTheTasksThatAreOurs()
   {
      const string otherSid = "S-1-5-21-100-200-300-1002";
      const string foreignSid = "S-1-5-21-100-200-300-1003";
      const string unreadableSid = "S-1-5-21-100-200-300-1004";
      using var setup = new Setup();
      setup.Scheduler.Tasks[TaskName] = setup.Rules.CreateTaskXml(Sid);
      setup.Scheduler.Tasks["QQQ Print Agent " + otherSid] = setup.Rules.CreateTaskXml(otherSid);
      setup.Scheduler.Tasks["QQQ Print Agent " + foreignSid] = setup.Rules.CreateTaskXml(foreignSid).Replace("--tray", "--other", StringComparison.Ordinal);
      setup.Scheduler.Tasks["QQQ Print Agent " + unreadableSid] = setup.Rules.CreateTaskXml(unreadableSid);
      setup.Scheduler.Unreadable.Add("QQQ Print Agent " + unreadableSid);
      setup.Scheduler.Tasks["QQQ Print Agent backup"] = setup.Rules.CreateTaskXml(Sid);
      setup.Scheduler.Tasks["Microsoft\\Windows\\Defrag\\ScheduledDefrag"] = "<Task />";
      setup.Scheduler.ExtraListing.Add("\"\\QQQ Print Agent " + Sid);
      await setup.Rules.RemoveInstalledTasksAsync();
      Assert.Equal(new[] { "Microsoft\\Windows\\Defrag\\ScheduledDefrag", "QQQ Print Agent " + foreignSid, "QQQ Print Agent " + unreadableSid, "QQQ Print Agent backup" }, setup.Scheduler.Tasks.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Equal(new[] { "/Query", "/FO", "CSV", "/NH" }, setup.Scheduler.Runs[0]);
      Assert.Equal(new[] { "\\" + TaskName, "\\QQQ Print Agent " + otherSid }, setup.Scheduler.Runs.Where(run => run[0] is "/Delete").Select(run => run[2]), StringComparer.Ordinal);
      Assert.All(setup.Scheduler.Runs.Where(run => run[0] is "/Delete"), run => Assert.Equal(new[] { "/Delete", "/TN", run[2], "/F" }, run));
      Assert.DoesNotContain(setup.Scheduler.Runs, run => run.Contains("\\QQQ Print Agent backup"));
   }



   /// <summary>Uninstall stops with a clear failure when the task list cannot be read, and when a task verified as ours cannot be deleted.</summary>
   [Fact]
   public async Task UninstallReportsAListThatCannotBeReadAndATaskThatCannotBeDeleted()
   {
      using var setup = new Setup();
      setup.Scheduler.Tasks[TaskName] = setup.Rules.CreateTaskXml(Sid);
      setup.Scheduler.Refuses["/Delete"] = 1;
      Assert.Equal("Could not remove a verified QQQ Print Agent startup task.", (await Assert.ThrowsAsync<InvalidOperationException>(setup.Rules.RemoveInstalledTasksAsync)).Message);
      setup.Scheduler.Refuses["/Query"] = 1;
      Assert.Equal("Could not inspect startup tasks during uninstall.", (await Assert.ThrowsAsync<InvalidOperationException>(setup.Rules.RemoveInstalledTasksAsync)).Message);
      Assert.True(setup.Scheduler.Tasks.ContainsKey(TaskName));
   }



   /// <summary>
   /// Windows may store an account name where the task was registered with a security identifier. The task is ours when
   /// that name stands for exactly the identifier in the task's name; a name that stands for another account, a name that
   /// stands for none, and a different identifier written out are all refused, and an identifier is never looked up as a name.
   /// </summary>
   [Fact]
   public void AccountNameInATaskMustStandForTheIdentifierInItsName()
   {
      using var setup = new Setup();
      setup.Scheduler.Accounts["WORKSTATION\\operator"] = Sid;
      setup.Scheduler.Accounts["WORKSTATION\\someone"] = "S-1-5-21-100-200-300-2000";
      Assert.True(setup.Rules.IsOwnedTaskDefinition("\\" + TaskName, WithUser(setup, "WORKSTATION\\operator")));
      Assert.False(setup.Rules.IsOwnedTaskDefinition("\\" + TaskName, WithUser(setup, "WORKSTATION\\someone")));
      Assert.False(setup.Rules.IsOwnedTaskDefinition("\\" + TaskName, WithUser(setup, "WORKSTATION\\nobody")));
      Assert.False(setup.Rules.IsOwnedTaskDefinition("\\" + TaskName, WithUser(setup, " ")));
      // The name is asked about once for the principal and once for the trigger; a refusal at the principal ends the check.
      Assert.Equal(new[] { "WORKSTATION\\operator", "WORKSTATION\\operator", "WORKSTATION\\someone", "WORKSTATION\\nobody" }, setup.Scheduler.Resolved, StringComparer.Ordinal);
      Assert.False(setup.Rules.IsOwnedTaskDefinition("\\" + TaskName, WithUser(setup, "S-1-5-21-100-200-300-2000")));
      Assert.Equal(4, setup.Scheduler.Resolved.Count);
   }



   /// <summary>
   /// Only the real installed companion counts as installed: the exact install path, an existing file, and no link at the
   /// file, its folder or the product folder. A missing file, a folder where the program should be, another path, no path,
   /// a link at any of the three levels and a parent that cannot be found are all refused.
   /// </summary>
   [Fact]
   public void OnlyTheLinkFreeProgramAtTheInstallPathIsTheInstalledCompanion()
   {
      using var setup = new Setup();
      string application = Path.GetDirectoryName(setup.Installed)!;
      string product = Path.GetDirectoryName(application)!;
      Assert.True(setup.Rules.IsInstalledCompanion(setup.Installed));
      Assert.True(setup.Rules.IsInstalledCompanion(setup.Installed.ToUpperInvariant()));
      Assert.False(setup.Rules.IsInstalledCompanion(null));
      Assert.False(setup.Rules.IsInstalledCompanion(Path.Combine(setup.Root, "elsewhere", "Qqq.PrintAgent.exe")));
      foreach(string link in new[] { setup.Installed, application, product })
      {
         FileAttributes before = setup.Scheduler.Paths[link];
         setup.Scheduler.Paths[link] = before | FileAttributes.ReparsePoint;
         Assert.False(setup.Rules.IsInstalledCompanion(setup.Installed));
         setup.Scheduler.Paths[link] = before;
      }
      foreach(string missing in new[] { setup.Installed, application, product })
      {
         FileAttributes before = setup.Scheduler.Paths[missing];
         _ = setup.Scheduler.Paths.Remove(missing);
         Assert.False(setup.Rules.IsInstalledCompanion(setup.Installed));
         setup.Scheduler.Paths[missing] = before;
      }
      setup.Scheduler.Paths[setup.Installed] = FileAttributes.Directory;
      Assert.False(setup.Rules.IsInstalledCompanion(setup.Installed));
   }



   /// <summary>
   /// Every command the rules send, whether it queries, creates, deletes or lists, is run with the task scheduler's
   /// program by its full path in the Windows system folder and never by a bare name, so a program called schtasks
   /// somewhere else on the search path can never be started with administrator rights in its place.
   /// </summary>
   [Fact]
   public async Task EveryCommandRunsTheSchedulerFromTheSystemFolder()
   {
      using var setup = new Setup();
      _ = await setup.Rules.SetEnabledAsync(true);
      _ = await setup.Rules.SetEnabledAsync(false);
      setup.Scheduler.Tasks[TaskName] = setup.Rules.CreateTaskXml(Sid);
      await setup.Rules.RemoveInstalledTasksAsync();
      Assert.Equal(new[] { "/Create", "/Delete", "/Query" }, setup.Scheduler.Runs.Select(run => run[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Contains(setup.Scheduler.Runs, run => run.Contains("/FO", StringComparer.Ordinal));
      Assert.Equal(setup.Scheduler.Runs.Count, setup.Scheduler.Programs.Count);
      Assert.All(setup.Scheduler.Programs, program =>
      {
         Assert.True(Path.IsPathFullyQualified(program));
         Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.System), Path.GetDirectoryName(program), ignoreCase: true);
         Assert.Equal("schtasks.exe", Path.GetFileName(program));
      });
      Assert.Equal(AgentStartupTasks.SchedulerProgram, Assert.Single(setup.Scheduler.Programs.Distinct(StringComparer.Ordinal)));
   }



   /// <summary>The generated definition with the user in both places replaced by the given account text.</summary>
   private static string WithUser(Setup setup, string account)
   {
      var document = XDocument.Parse(setup.Rules.CreateTaskXml(Sid));
      Assert.Single(document.Descendants(Ns + "Principal")).Element(Ns + "UserId")!.Value = account;
      Assert.Single(document.Descendants(Ns + "LogonTrigger")).Element(Ns + "UserId")!.Value = account;
      return document.ToString();
   }



   /// <summary>The rules under test with an in-memory scheduler, an install path and a setup folder under a temporary root, and an administrator running the installed companion as the caller.</summary>
   private sealed class Setup : IDisposable
   {
      /// <summary>The temporary root everything in this setup lives under; only the setup folder is ever created.</summary>
      internal string Root { get; } = Path.Combine(Path.GetTempPath(), "qqq-startup-rules-" + Guid.NewGuid().ToString("N"));



      /// <summary>The path the rules treat as the installed companion.</summary>
      internal string Installed { get; }



      /// <summary>The folder task definitions are staged in.</summary>
      internal string SetupDirectory { get; }



      /// <summary>The in-memory scheduler the rules talk to.</summary>
      internal FakeTaskScheduler Scheduler { get; } = new();



      /// <summary>The rules under test.</summary>
      internal AgentStartupTasks Rules { get; }



      /// <summary>The folders the rules asked to have checked as protected state, in order.</summary>
      internal List<string> EnsuredFolders { get; } = [];



      /// <summary>The staged files the rules asked to have checked, in order.</summary>
      internal List<string> ValidatedFiles { get; } = [];



      /// <summary>When set, the folder check refuses with this.</summary>
      internal Exception? FolderRefusal { get; set; }



      /// <summary>When set, the staged-file check refuses with this.</summary>
      internal Exception? FileRefusal { get; set; }



      /// <summary>Describes an installed, link-free companion to the fake and makes the caller that companion.</summary>
      internal Setup()
      {
         Installed = Path.Combine(Root, "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");
         SetupDirectory = Path.Combine(Root, "setup");
         Scheduler.Paths[Installed] = FileAttributes.Archive;
         Scheduler.Paths[Path.GetDirectoryName(Installed)!] = FileAttributes.Directory;
         Scheduler.Paths[Path.GetDirectoryName(Path.GetDirectoryName(Installed)!)!] = FileAttributes.Directory;
         Scheduler.Caller = new StartupCaller(Sid, IsAdministrator: true, ProcessPath: Installed);
         Rules = new AgentStartupTasks(Scheduler, Installed, new ProtectedStaging(SetupDirectory, folder =>
         {
            EnsuredFolders.Add(folder);
            if(FolderRefusal is not null)
            { throw FolderRefusal; }
         }, file =>
         {
            ValidatedFiles.Add(file);
            if(FileRefusal is not null)
            { throw FileRefusal; }
         }));
      }



      /// <summary>Removes the temporary root if a test created the setup folder.</summary>
      public void Dispose()
      {
         if(Directory.Exists(Root))
         { Directory.Delete(Root, recursive: true); }
      }
   }
}
