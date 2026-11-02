/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;


namespace Qqq.PrintAgent.Windows;

/// <summary>Explicitly opts the current administrator into an interactive sign-in task for the installed companion.</summary>
public static partial class AgentStartup
{
   private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";



   /// <summary>The only program the sign-in task may launch: the companion at its fixed machine-wide install path, so the task can never be pointed at another executable.</summary>
   private static string InstalledExecutable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");



   /// <summary>Query only; never registers a task. Call off the UI thread.</summary>
   public static AgentStartupStatus GetStatus() => GetStatusAsync().GetAwaiter().GetResult();



   /// <summary>
   /// Reads the startup task's state for the current administrator. A non-administrator gets an explanation instead of a
   /// state. A task with this name that is not exactly ours is reported as foreign and never treated as enabled, so the
   /// preference never claims control of someone else's task.
   /// </summary>
   private static async Task<AgentStartupStatus> GetStatusAsync()
   {
      using var identity = WindowsIdentity.GetCurrent();
      if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
      {
         return new AgentStartupStatus(false, false, "Administrator authorization is required to configure sign-in startup.");
      }
      bool installed = IsInstalledCompanion(Environment.ProcessPath);
      (int code, string output) = await RunSchedulerAsync("/Query", "/TN", TaskName(identity), "/XML").ConfigureAwait(false);
      bool enabled = false;
      if(code == 0)
      {
         if(!IsOwnedTaskDefinition(TaskName(identity), output))
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
   public static async Task<AgentStartupStatus> SetEnabledAsync(bool enabled)
   {
      using var identity = WindowsIdentity.GetCurrent();
      if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
      {
         throw new UnauthorizedAccessException("Configure sign-in startup from the elevated installed companion.");
      }
      (int existingCode, string existingXml) = await RunSchedulerAsync("/Query", "/TN", TaskName(identity), "/XML").ConfigureAwait(false);
      if(existingCode == 0 && !IsOwnedTaskDefinition(TaskName(identity), existingXml))
      {
         throw new InvalidOperationException("A different task definition uses this startup name. It was left unchanged; inspect it in Windows Task Scheduler.");
      }
      if(!enabled)
      {
         if(existingCode == 0)
         {
            (int deleteCode, _) = await RunSchedulerAsync("/Delete", "/TN", TaskName(identity), "/F").ConfigureAwait(false);
            if(deleteCode != 0)
            {
               throw new InvalidOperationException("Windows could not remove the sign-in task. Open Task Scheduler to inspect it.");
            }
         }
      }
      else
      {
         if(!IsInstalledCompanion(Environment.ProcessPath))
         {
            throw new InvalidOperationException("Only the installed Program Files companion can enable elevated sign-in startup.");
         }
         string setupDirectory = Path.Combine(AgentStore.DefaultDirectory, "setup");
         Qqq.PrintAgent.Core.MachineStateDirectory.EnsureForPath(setupDirectory);
         Directory.CreateDirectory(setupDirectory);
         string definition = Path.Combine(setupDirectory, "startup-" + Guid.NewGuid().ToString("N") + ".xml");
         try
         {
            using FileStream heldDefinition = CreateHeldDefinition(definition, CreateTaskXml(identity.User?.Value ?? throw new InvalidOperationException("Current user has no SID.")));
            Qqq.PrintAgent.Core.MachineStateDirectory.ValidateFile(definition);
            (int createCode, _) = await RunSchedulerAsync("/Create", "/TN", TaskName(identity), "/XML", definition, "/F").ConfigureAwait(false);
            if(createCode != 0)
            {
               throw new InvalidOperationException("Windows could not register the sign-in task. Check Task Scheduler and administrator permissions.");
            }
            (int verifyCode, string registered) = await RunSchedulerAsync("/Query", "/TN", TaskName(identity), "/XML").ConfigureAwait(false);
            if(verifyCode != 0 || !IsOwnedTaskDefinition(TaskName(identity), registered))
            {
               throw new InvalidOperationException("The registered startup task could not be verified. Inspect Windows Task Scheduler; an unexpected task was left unchanged.");
            }
         }
         finally
         {
            File.Delete(definition);
         }
      }
      return await GetStatusAsync().ConfigureAwait(false);
   }



   /// <summary>The task name for one user: a fixed prefix plus the user's SID, so every administrator has a separate task and the name identifies its owner.</summary>
   private static string TaskName(WindowsIdentity identity) => "QQQ Print Agent " + (identity.User?.Value ?? throw new InvalidOperationException("Current user has no SID."));



   /// <summary>
   /// Writes the task definition and returns it open for reading, with sharing set so Task Scheduler's own reader can open
   /// it while this handle stays held. Holding the handle in the validated protected folder prevents the file being replaced
   /// between validation and registration.
   /// </summary>
   internal static FileStream CreateHeldDefinition(string path, string xml)
   {
      Qqq.PrintAgent.Core.MachineStateDirectory.EnsureForPath(path);
      // The validated protected parent prevents replacement while switching from the
      // exclusive writer to a read handle compatible with Task Scheduler's own reader.
      using(FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
      {
         byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
         stream.Write(bytes);
         stream.Flush(flushToDisk: true);
      }
      return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
   }



   /// <summary>Full-uninstall cleanup removes only task definitions that exactly match this application's fixed identity.</summary>
   public static async Task RemoveInstalledTasksAsync()
   {
      using var identity = WindowsIdentity.GetCurrent();
      if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
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
   internal static bool IsOwnedTaskDefinition(string taskName, string xml)
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
            && string.Equals(actions[0].Element(TaskNamespace + "Command")?.Value, InstalledExecutable, StringComparison.OrdinalIgnoreCase)
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
   private static bool MatchesUserSid(string? account, string expectedSid)
   {
      if(string.Equals(account, expectedSid, StringComparison.Ordinal))
      {
         return true;
      }
      if(string.IsNullOrWhiteSpace(account) || account.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
      {
         return false;
      }
      try
      {
         // Task Scheduler may serialize an account name even when registration
         // used its SID. Ownership still requires the exact original identity.
         var resolved = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
         return string.Equals(resolved.Value, expectedSid, StringComparison.Ordinal);
      }
      catch(Exception exception) when(exception is IdentityNotMappedException or ArgumentException or System.Security.SecurityException)
      {
         return false;
      }
   }



   /// <summary>Matches only this application's task names (the fixed prefix and a SID), with a one second match limit, so an uninstall scan can never act on an unrelated task.</summary>
   [GeneratedRegex("""^\\?QQQ Print Agent (?<sid>S-\d+(?:-\d+)+)$""", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
   private static partial Regex OwnedTaskName();



   /// <summary>
   /// True only for the real installed companion: it must be the Program Files executable at the exact install path, exist, and
   /// neither it nor its two parent folders may be a reparse point, so a development copy or a redirected folder cannot gain the elevated task.
   /// </summary>
   internal static bool IsInstalledCompanion(string? executable)
   {
      if(executable is null || !string.Equals(Path.GetFullPath(executable), Path.GetFullPath(InstalledExecutable), StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
      {
         return false;
      }
      return !File.GetAttributes(executable).HasFlag(FileAttributes.ReparsePoint)
         && !File.GetAttributes(Path.GetDirectoryName(executable)!).HasFlag(FileAttributes.ReparsePoint)
         && !File.GetAttributes(Path.GetDirectoryName(Path.GetDirectoryName(executable))!).HasFlag(FileAttributes.ReparsePoint);
   }



   /// <summary>
   /// Builds the Task Scheduler definition for one user: an interactive logon trigger and principal at the highest available
   /// level, and a single action running the installed companion with "--tray". It must stay in step with
   /// <c>IsOwnedTaskDefinition</c>, which accepts only this shape.
   /// </summary>
   internal static string CreateTaskXml(string userSid)
   {
      XNamespace ns = TaskNamespace;
      XElement Element(string name, object content) => new(ns + name, content);
      XDocument document = new(new XDeclaration("1.0", "utf-16", null),
         new XElement(ns + "Task", new XAttribute("version", "1.4"),
            Element("RegistrationInfo", Element("Description", "Explicitly enabled by this administrator to show QQQ Print Agent in the tray at sign-in.")),
            Element("Triggers", new XElement(ns + "LogonTrigger", Element("Enabled", "true"), Element("UserId", userSid))),
            Element("Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"), Element("UserId", userSid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", Element("MultipleInstancesPolicy", "IgnoreNew"), Element("DisallowStartIfOnBatteries", "false"), Element("StopIfGoingOnBatteries", "false"), Element("Enabled", "true"), Element("ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(ns + "Exec", Element("Command", InstalledExecutable), Element("Arguments", "--tray"), Element("WorkingDirectory", Path.GetDirectoryName(InstalledExecutable)!)))));
      return document.Declaration + Environment.NewLine + document;
   }



   /// <summary>
   /// Runs schtasks.exe from the system folder with an argument list (never a shell string) and a 15 second limit, and returns
   /// its exit code and output. A hung scheduler is killed with its process tree and reported as a timeout with advice to refresh.
   /// </summary>
   private static async Task<(int Code, string Output)> RunSchedulerAsync(params string[] arguments)
   {
      ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach(string argument in arguments)
      {
         start.ArgumentList.Add(argument);
      }
      using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows Task Scheduler administration.");
      Task<string> output = process.StandardOutput.ReadToEndAsync();
      Task<string> error = process.StandardError.ReadToEndAsync();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
      try
      {
         await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
      }
      catch(OperationCanceledException exception)
      {
         process.Kill(entireProcessTree: true);
         throw new TimeoutException("Windows Task Scheduler did not respond within 15 seconds. Refresh the preference to check its current state.", exception);
      }
      string result = await output.ConfigureAwait(false);
      _ = await error.ConfigureAwait(false);
      return (process.ExitCode, result);
   }
}
