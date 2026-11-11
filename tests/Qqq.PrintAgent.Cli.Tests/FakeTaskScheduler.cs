/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A task scheduler that exists only in memory. It answers the four commands the startup rules send (query one task, list
/// all, create from a definition file, delete) from a dictionary of registered tasks, records every command it was sent,
/// and can be told to fail in each way the real one can: not start, hang, refuse a command, or store a task differently
/// from the definition it was given. Nothing on this computer is read or changed.
/// </summary>
internal sealed class FakeTaskScheduler : ITaskScheduler
{
   /// <summary>The user the rules are asked on behalf of in these tests.</summary>
   internal const string UserSid = "S-1-5-21-100-200-300-1001";



   /// <summary>Who is asking; an administrator running the installed companion unless a test says otherwise.</summary>
   internal StartupCaller Caller { get; set; } = new(UserSid, IsAdministrator: true, ProcessPath: null);



   /// <summary>The registered tasks: name (without the leading backslash) to definition.</summary>
   internal Dictionary<string, string> Tasks { get; } = new(StringComparer.Ordinal);



   /// <summary>Lines the task list prints in addition to one line per registered task.</summary>
   internal List<string> ExtraListing { get; } = [];



   /// <summary>Account names this computer knows, with the security identifier each stands for.</summary>
   internal Dictionary<string, string> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>What exists on disk, as far as the rules may ask: path to attributes.</summary>
   internal Dictionary<string, FileAttributes> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>Every command sent, in order, as its argument list.</summary>
   internal List<string[]> Runs { get; } = [];



   /// <summary>The program each command was to be run with, in order.</summary>
   internal List<string> Programs { get; } = [];



   /// <summary>The time limit given with the last command.</summary>
   internal TimeSpan? LastLimit { get; private set; }



   /// <summary>Account names the rules asked to have resolved.</summary>
   internal List<string> Resolved { get; } = [];



   /// <summary>When true, the program cannot be started at all.</summary>
   internal bool CannotStart { get; set; }



   /// <summary>A command (its first argument, such as "/Create") that hangs and is stopped at the limit.</summary>
   internal string? HangsOn { get; set; }



   /// <summary>Exit codes to answer a command with instead of carrying it out, by first argument.</summary>
   internal Dictionary<string, int> Refuses { get; } = new(StringComparer.Ordinal);



   /// <summary>Task names whose definition cannot be read although the task is listed.</summary>
   internal HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);



   /// <summary>Changes a definition as it is stored, the way Windows may rewrite one; unchanged by default.</summary>
   internal Func<string, string> StoreAs { get; set; } = definition => definition;



   /// <summary>The path, text and first bytes of the definition file handed to the last create command, read while the command ran.</summary>
   internal (string Path, string Text, byte[] Start)? Definition { get; private set; }



   /// <summary>True when the definition file could not be deleted or rewritten while the create command ran.</summary>
   internal bool DefinitionWasHeld { get; private set; }



   /// <summary>Returns the caller the test configured.</summary>
   public StartupCaller CurrentCaller() => Caller;



   /// <summary>Records the command and answers it from the registered tasks, or fails the way the test asked.</summary>
   public Task<TaskSchedulerRun?> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan limit)
   {
      Programs.Add(program);
      Runs.Add([.. arguments]);
      LastLimit = limit;
      if(CannotStart)
      { return Task.FromResult<TaskSchedulerRun?>(null); }
      if(string.Equals(HangsOn, arguments[0], StringComparison.Ordinal))
      { return Task.FromResult<TaskSchedulerRun?>(new TaskSchedulerRun(0, "", new OperationCanceledException("Synthetic scheduler hang."))); }
      if(Refuses.TryGetValue(arguments[0], out int refusal))
      { return Task.FromResult<TaskSchedulerRun?>(new TaskSchedulerRun(refusal, "")); }
      return Task.FromResult<TaskSchedulerRun?>(arguments[0] switch
      {
         "/Query" when arguments.Contains("/FO", StringComparer.Ordinal) => new TaskSchedulerRun(0, string.Join('\n', Tasks.Keys.Select(name => $"\"\\{name}\",\"N/A\",\"Ready\"\r").Concat(ExtraListing))),
         "/Query" => Query(arguments[2]),
         "/Create" => Create(arguments[2], arguments[4]),
         "/Delete" => new TaskSchedulerRun(Tasks.Remove(arguments[2].TrimStart('\\')) ? 0 : 1, ""),
         _ => throw new InvalidOperationException("The startup rules sent a command this fake does not know: " + arguments[0]),
      });
   }



   /// <summary>Answers a query for one task: its definition, or exit code 1 when there is none or it cannot be read.</summary>
   private TaskSchedulerRun Query(string name)
   {
      string key = name.TrimStart('\\');
      return Tasks.TryGetValue(key, out string? definition) && !Unreadable.Contains(key) ? new TaskSchedulerRun(0, definition) : new TaskSchedulerRun(1, "");
   }



   /// <summary>Registers a task from its definition file, noting what the file held and whether it was protected from change while being read.</summary>
   private TaskSchedulerRun Create(string name, string definitionPath)
   {
      Definition = (definitionPath, File.ReadAllText(definitionPath), File.ReadAllBytes(definitionPath)[..2]);
      try
      {
         File.Delete(definitionPath);
         DefinitionWasHeld = false;
      }
      catch(IOException)
      {
         DefinitionWasHeld = true;
      }
      Tasks[name.TrimStart('\\')] = StoreAs(Definition.Value.Text);
      return new TaskSchedulerRun(0, "");
   }



   /// <summary>Resolves a name the test registered, and notes that it was asked.</summary>
   public string? ResolveAccountSid(string account)
   {
      Resolved.Add(account);
      return Accounts.GetValueOrDefault(account);
   }



   /// <summary>Reports what the test said exists at the path.</summary>
   public FileAttributes? AttributesOf(string path) => Paths.TryGetValue(path, out FileAttributes attributes) ? attributes : null;
}
