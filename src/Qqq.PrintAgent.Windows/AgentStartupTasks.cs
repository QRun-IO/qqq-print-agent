/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The rules of the sign-in startup task: what the task says, what counts as this application's task, who may change it
/// and when one is created, replaced or removed. Everything it needs from Windows comes through
/// <see cref="ITaskScheduler"/>, so the rules can be run against a fake; the program the task may launch and the
/// protected place the definition is staged in are given, so a test can point them at a temporary place.
/// </summary>
internal sealed partial class AgentStartupTasks(ITaskScheduler scheduler, string installedExecutable, ProtectedStaging staging)
{
   /// <summary>How long one run of the task scheduler may take before it is stopped and reported as not responding.</summary>
   internal static readonly TimeSpan SchedulerLimit = TimeSpan.FromSeconds(15);

   private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";



   /// <summary>
   /// Reads the startup task's state for the current administrator. A non-administrator gets an explanation instead of a
   /// state. A task with this name that is not exactly ours is reported as foreign and never treated as enabled, so the
   /// preference never claims control of someone else's task.
   /// </summary>
   internal async Task<AgentStartupStatus> GetStatusAsync()
   {
      StartupCaller caller = scheduler.CurrentCaller();
      if(!caller.IsAdministrator)
      {
         return new AgentStartupStatus(false, false, "Administrator authorization is required to configure sign-in startup.");
      }
      bool installed = IsInstalledCompanion(caller.ProcessPath);
      (int code, string output) = await RunSchedulerAsync("/Query", "/TN", TaskName(caller), "/XML").ConfigureAwait(false);
      bool enabled = false;
      if(code == 0)
      {
         if(!IsOwnedTaskDefinition(TaskName(caller), output))
         {
            return new AgentStartupStatus(false, false, "A different task definition uses this startup name. Inspect it in Windows Task Scheduler before changing this preference.");
         }
         var document = XDocument.Parse(output);
         enabled = !string.Equals(document.Root?.Element(TaskNamespace + "Settings")?.Element(TaskNamespace + "Enabled")?.Value, "false", StringComparison.OrdinalIgnoreCase);
      }
      return new AgentStartupStatus(enabled, installed || enabled, installed
         ? "Starts the installed companion in your tray at your next sign-in. The service already starts with Windows."
         : "Enable this from the MSI-installed companion. Development and portable executables cannot create an elevated sign-in task.");
   }



   /// <summary>
   /// Only an explicit user action calls this method; enabling authorizes an elevated task for the current administrator.
   /// Disabling deletes only a task that is exactly ours. Enabling is allowed only from the installed companion, writes the
   /// task definition to a protected folder, registers it, then reads it back and requires it to match, so a task that
   /// Windows stored differently is reported instead of trusted. The temporary definition is always deleted.
   /// </summary>
   internal async Task<AgentStartupStatus> SetEnabledAsync(bool enabled)
   {
      StartupCaller caller = scheduler.CurrentCaller();
      if(!caller.IsAdministrator)
      {
         throw new UnauthorizedAccessException("Configure sign-in startup from the elevated installed companion.");
      }
      (int existingCode, string existingXml) = await RunSchedulerAsync("/Query", "/TN", TaskName(caller), "/XML").ConfigureAwait(false);
      if(existingCode == 0 && !IsOwnedTaskDefinition(TaskName(caller), existingXml))
      {
         throw new InvalidOperationException("A different task definition uses this startup name. It was left unchanged; inspect it in Windows Task Scheduler.");
      }
      if(!enabled)
      {
         if(existingCode == 0)
         {
            (int deleteCode, _) = await RunSchedulerAsync("/Delete", "/TN", TaskName(caller), "/F").ConfigureAwait(false);
            if(deleteCode != 0)
            {
               throw new InvalidOperationException("Windows could not remove the sign-in task. Open Task Scheduler to inspect it.");
            }
         }
      }
      else
      {
         if(!IsInstalledCompanion(caller.ProcessPath))
         {
            throw new InvalidOperationException("Only the installed Program Files companion can enable elevated sign-in startup.");
         }
         await RegisterAsync(caller).ConfigureAwait(false);
      }
      return await GetStatusAsync().ConfigureAwait(false);
   }



   /// <summary>
   /// Stages the task definition in the protected setup folder, holds it open while the scheduler registers it, and then
   /// reads the registered task back: a task Windows stored differently from ours is reported, never trusted. The staged
   /// definition is deleted whatever happens.
   /// </summary>
   private async Task RegisterAsync(StartupCaller caller)
   {
      staging.EnsureFolder(staging.Directory);
      Directory.CreateDirectory(staging.Directory);
      string definition = Path.Combine(staging.Directory, "startup-" + Guid.NewGuid().ToString("N") + ".xml");
      try
      {
         using FileStream heldDefinition = AgentStartup.CreateHeldDefinition(definition, CreateTaskXml(caller.UserSid ?? throw new InvalidOperationException("Current user has no SID.")));
         staging.ValidateFile(definition);
         (int createCode, _) = await RunSchedulerAsync("/Create", "/TN", TaskName(caller), "/XML", definition, "/F").ConfigureAwait(false);
         if(createCode != 0)
         {
            throw new InvalidOperationException("Windows could not register the sign-in task. Check Task Scheduler and administrator permissions.");
         }
         (int verifyCode, string registered) = await RunSchedulerAsync("/Query", "/TN", TaskName(caller), "/XML").ConfigureAwait(false);
         if(verifyCode != 0 || !IsOwnedTaskDefinition(TaskName(caller), registered))
         {
            throw new InvalidOperationException("The registered startup task could not be verified. Inspect Windows Task Scheduler; an unexpected task was left unchanged.");
         }
      }
      finally
      {
         File.Delete(definition);
      }
   }



   /// <summary>The task name for one user: a fixed prefix plus the user's SID, so every administrator has a separate task and the name identifies its owner.</summary>
   private static string TaskName(StartupCaller caller) => "QQQ Print Agent " + (caller.UserSid ?? throw new InvalidOperationException("Current user has no SID."));



   /// <summary>Full-uninstall cleanup removes only task definitions that exactly match this application's fixed identity.</summary>
   internal async Task RemoveInstalledTasksAsync()
   {
      if(!scheduler.CurrentCaller().IsAdministrator)
      {
         throw new UnauthorizedAccessException("Removing machine installation startup tasks requires administrator authorization.");
      }
      (int queryCode, string listing) = await RunSchedulerAsync("/Query", "/FO", "CSV", "/NH").ConfigureAwait(false);
      if(queryCode != 0)
      {
         throw new InvalidOperationException("Could not inspect startup tasks during uninstall.");
      }
      foreach(string line in listing.Split('\n'))
      {
         // Task Scheduler quotes CSV fields. Only this fixed prefix and a SID can become a candidate.
         if(!line.StartsWith("\"\\QQQ Print Agent ", StringComparison.Ordinal))
         {
            continue;
         }
         int closingQuote = line.IndexOf('"', 1);
         if(closingQuote < 0)
         {
            continue;
         }
         string name = line[1..closingQuote];
         if(!OwnedTaskName().IsMatch(name))
         {
            continue;
         }
         (int definitionCode, string definition) = await RunSchedulerAsync("/Query", "/TN", name, "/XML").ConfigureAwait(false);
         if(definitionCode != 0 || !IsOwnedTaskDefinition(name, definition))
         {
            continue;
         }
         (int deleteCode, _) = await RunSchedulerAsync("/Delete", "/TN", name, "/F").ConfigureAwait(false);
         if(deleteCode != 0)
         {
            throw new InvalidOperationException("Could not remove a verified QQQ Print Agent startup task.");
         }
      }
   }



   /// <summary>
   /// Decides whether a registered task is exactly the one this application creates: a name with a user SID, a single
   /// principal that logs on interactively at the highest available level, a single logon trigger and a single action that runs the
   /// installed companion with "--tray". Anything else, including unparsable XML, is not ours and is never changed or deleted.
   /// </summary>
   internal bool IsOwnedTaskDefinition(string taskName, string xml)
   {
      Match name = OwnedTaskName().Match(taskName);
      if(!name.Success)
      {
         return false;
      }
      try
      {
         var document = XDocument.Parse(xml);
         XElement? root = document.Root;
         XElement[] principals = root?.Element(TaskNamespace + "Principals")?.Elements().ToArray() ?? [];
         XElement[] triggers = root?.Element(TaskNamespace + "Triggers")?.Elements().ToArray() ?? [];
         XElement[] actions = root?.Element(TaskNamespace + "Actions")?.Elements().ToArray() ?? [];
         string sid = name.Groups["sid"].Value;
         return root?.Name == TaskNamespace + "Task" && principals.Length == 1 && triggers.Length == 1 && actions.Length == 1
            && principals[0].Name == TaskNamespace + "Principal"
            && MatchesUserSid(principals[0].Element(TaskNamespace + "UserId")?.Value, sid)
            && string.Equals(principals[0].Element(TaskNamespace + "LogonType")?.Value, "InteractiveToken", StringComparison.Ordinal)
            && string.Equals(principals[0].Element(TaskNamespace + "RunLevel")?.Value, "HighestAvailable", StringComparison.Ordinal)
            && triggers[0].Name == TaskNamespace + "LogonTrigger"
            && MatchesUserSid(triggers[0].Element(TaskNamespace + "UserId")?.Value, sid)
            && actions[0].Name == TaskNamespace + "Exec"
            && string.Equals(actions[0].Element(TaskNamespace + "Command")?.Value, installedExecutable, StringComparison.OrdinalIgnoreCase)
            && string.Equals(actions[0].Element(TaskNamespace + "Arguments")?.Value, "--tray", StringComparison.Ordinal);
      }
      catch(XmlException)
      {
         return false;
      }
   }



   /// <summary>
   /// True when a task's user element is the expected SID, or an account name that resolves to exactly that SID, because Task
   /// Scheduler may store a name even when the task was registered with a SID. A different SID, or a name that does not
   /// resolve, is rejected, so ownership still requires the exact original identity.
   /// </summary>
   private bool MatchesUserSid(string? account, string expectedSid)
   {
      if(string.Equals(account, expectedSid, StringComparison.Ordinal))
      {
         return true;
      }
      if(string.IsNullOrWhiteSpace(account) || account.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
      {
         return false;
      }
      // Task Scheduler may serialize an account name even when registration
      // used its SID. Ownership still requires the exact original identity.
      return string.Equals(scheduler.ResolveAccountSid(account), expectedSid, StringComparison.Ordinal);
   }



   /// <summary>Matches only this application's task names (the fixed prefix and a SID), with a one second match limit, so an uninstall scan can never act on an unrelated task.</summary>
   [GeneratedRegex("""^\\?QQQ Print Agent (?<sid>S-\d+(?:-\d+)+)$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex OwnedTaskName();



   /// <summary>
   /// True only for the real installed companion: it must be the Program Files executable at the exact install path, exist, and
   /// neither it nor its two parent folders may be a reparse point, so a development copy or a redirected folder cannot gain the elevated task.
   /// </summary>
   internal bool IsInstalledCompanion(string? executable)
   {
      if(executable is null || !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(installedExecutable), StringComparison.OrdinalIgnoreCase)
         || scheduler.AttributesOf(executable) is not FileAttributes program || program.HasFlag(FileAttributes.Directory))
      {
         return false;
      }
      string folder = Path.GetDirectoryName(executable)!;
      return !program.HasFlag(FileAttributes.ReparsePoint)
         && scheduler.AttributesOf(folder) is FileAttributes application && !application.HasFlag(FileAttributes.ReparsePoint)
         && scheduler.AttributesOf(Path.GetDirectoryName(folder)!) is FileAttributes product && !product.HasFlag(FileAttributes.ReparsePoint);
   }



   /// <summary>
   /// Builds the Task Scheduler definition for one user: an interactive logon trigger and principal at the highest available
   /// level, and a single action running the installed companion with "--tray". It must stay in step with
   /// <c>IsOwnedTaskDefinition</c>, which accepts only this shape.
   /// </summary>
   internal string CreateTaskXml(string userSid)
   {
      XNamespace ns = TaskNamespace;
      XElement Element(string name, object content) => new(ns + name, content);
      XDocument document = new(new XDeclaration("1.0", "utf-16", null),
         new XElement(ns + "Task", new XAttribute("version", "1.4"),
            Element("RegistrationInfo", Element("Description", "Explicitly enabled by this administrator to show QQQ Print Agent in the tray at sign-in.")),
            Element("Triggers", new XElement(ns + "LogonTrigger", Element("Enabled", "true"), Element("UserId", userSid))),
            Element("Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"), Element("UserId", userSid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"), Element("StopIfGoingOnBatteries", "false"), Element("Enabled", "true"), Element("ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(ns + "Exec", Element("Command", installedExecutable), Element("Arguments", "--tray"), Element("WorkingDirectory", Path.GetDirectoryName(installedExecutable)!)))));
      return document.Declaration + Environment.NewLine + document;
   }



   /// <summary>
   /// Runs the task scheduler with an argument list and the 15 second limit, and returns its exit code and output. A scheduler
   /// that could not be started, or that hung and was stopped, is reported as such with advice to refresh.
   /// </summary>
   private async Task<(int Code, string Output)> RunSchedulerAsync(params string[] arguments)
   {
      TaskSchedulerRun run = await scheduler.RunAsync(arguments, SchedulerLimit).ConfigureAwait(false) ?? throw new InvalidOperationException("Could not start Windows Task Scheduler administration.");
      if(run.TimedOut is not null)
      {
         throw new TimeoutException("Windows Task Scheduler did not respond within 15 seconds. Refresh the preference to check its current state.", run.TimedOut);
      }
      return (run.ExitCode, run.Output);
   }
}
