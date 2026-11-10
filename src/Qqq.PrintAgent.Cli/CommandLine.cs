/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli;

/// <summary>
/// Everything qqq-print-agent.exe decides: which role an argument list selects, which request an operator command becomes,
/// what is printed where, and the exit code a script sees. The command line holds no state of its own and never talks to a
/// QQQ server: an operator command is one request to the installed service over its administrator pipe, because the service
/// owns the sign-in, the pairing and all printing. The outside world is reached only through the host, so tests run this
/// class against fakes.
/// </summary>
internal sealed class CommandLine(CommandLineHost host)
{
   /// <summary>The environment variable that supplies the one-time enrollment code to a script.</summary>
   internal const string EnrollmentCodeVariable = "QQQ_PRINT_ENROLLMENT_CODE";



   /// <summary>
   /// The help text: printed on standard output when asked for, and on standard error after a refused command line. Its
   /// line ends are made the platform's here, because the literal carries whatever this source file was checked out
   /// with and a console or a captured file should not get a mixture.
   /// </summary>
   internal static readonly string Usage = """
      Usage: qqq-print-agent <command>

      Commands:
        printers                                  List the printers the service can see, as JSON.
        enroll --server URL                       Pair this computer with a QQQ server using a one-time enrollment code.
        expose --printer ID --exposed true|false  Share a printer with QQQ, or stop sharing it.
        run                                       Ask the service to refresh its connection to QQQ.
        sign-out                                  End the service's QQQ session.
        service-status                            Show the service's connection status.
        snapshot                                  Show the service's state as JSON: connection, printers, recent jobs and logs.
        help                                      Show this text (also --help or -h, alone or after a command).

      Every command is carried out by the installed print service, which owns the sign-in, the pairing and all printing.
      Run from an administrator prompt, and sign in through the companion app first.
      The enrollment code is read from the QQQ_PRINT_ENROLLMENT_CODE environment variable, or else from standard input:
      at a Windows console it is prompted for and not shown; redirected input is read as one line, without a prompt.
      An unattended caller must set the variable or redirect input, or the prompt waits for a key.
      The code is never accepted on the command line.
      Exit code: 0 on success; 1 when the arguments are wrong, the service cannot be reached or the service reports an error.
      """.ReplaceLineEndings();



   /// <summary>Each operator command, with the pipe operation it becomes and the options it requires. Every listed option is mandatory, and nothing else is accepted.</summary>
   private static readonly Dictionary<string, (string Operation, string[] Options)> s_commands = new(StringComparer.Ordinal)
   {
      ["printers"] = ("printers", []),
      ["enroll"] = ("enroll", ["--server"]),
      ["expose"] = ("set-exposure", ["--printer", "--exposed"]),
      ["run"] = ("reconnect", []),
      ["sign-out"] = ("sign-out", []),
      ["service-status"] = ("status", []),
      ["snapshot"] = ("snapshot", []),
   };



   /// <summary>What a command line is told when it uses an option of the legacy mode that 2.0 removed, so a script written for 1.x learns why it fails.</summary>
   internal const string RemovedOptionsReason = "The options --service, --legacy-compat, --data-dir and --once were removed in 2.0: the command line only manages the installed service.";



   /// <summary>The options of the removed legacy mode. They are recognized only to explain the refusal.</summary>
   private static readonly HashSet<string> s_removedOptions = new(StringComparer.Ordinal) { "--service", "--legacy-compat", "--data-dir", "--once" };



   /// <summary>The names of the operator commands, exposed so a test can hold the help text to the commands that really exist.</summary>
   internal static IReadOnlyCollection<string> CommandNames => s_commands.Keys;



   /// <summary>
   /// Runs one invocation and returns the process exit code. Two argument shapes are internal and fixed: the MSI update
   /// helper and the browser sign-in callback forwarder, each started by another part of the product with exactly these
   /// arguments. Everything else is an operator command. This executable is not a printer worker: the service starts its
   /// own executable for that. A failure becomes one line on standard error and exit code 1; a timeout or cancellation
   /// does too, so a script never mistakes an operation that did not finish for one that succeeded.
   /// </summary>
   internal async Task<int> RunAsync(string[] args)
   {
      try
      {
         return args switch
         {
            ["--apply-update", string request] => await host.ApplyUpdateAsync(request),
            ["--auth-callback", string callback] => await ForwardSignInCallbackAsync(callback),
            _ => await ManageServiceAsync(args),
         };
      }
      catch(OperationCanceledException)
      {
         host.Error.WriteLine("The operation timed out or was canceled before completion.");
         return 1;
      }
      catch(Exception exception)
      {
         host.Error.WriteLine(exception.Message);
         return 1;
      }
   }



   /// <summary>Hands the browser's callback address to the companion that is waiting for it. Success is exit code 0; a refusal is thrown and reported by the caller.</summary>
   private async Task<int> ForwardSignInCallbackAsync(string callback)
   {
      await host.ForwardSignInCallbackAsync(callback, CancellationToken.None);
      return 0;
   }



   /// <summary>
   /// Carries out an operator command: help is printed here, and any other command is checked against the table, turned into
   /// one pipe request and sent to the service. The reply is printed on standard output as the service wrote it, also when
   /// it is a refusal. A reply that starts with "Error:" is the service refusing or failing the request, so it exits 1
   /// for every command.
   /// </summary>
   private async Task<int> ManageServiceAsync(string[] args)
   {
      if(AsksForHelp(args))
      {
         host.Output.WriteLine(Usage);
         return 0;
      }
      Dictionary<string, string> options = new(StringComparer.Ordinal);
      string? problem = FindProblem(args, options);
      if(problem is not null)
      {
         return RefuseArguments(problem);
      }

      ////////////////////////////////////////////////////////////////////////////
      // the enrollment code is asked for last, once the command line is known  //
      // to be valid, so a mistyped command never prompts for a single-use code //
      ////////////////////////////////////////////////////////////////////////////
      PipeRequest request = new(s_commands[args[0]].Operation, options.GetValueOrDefault("--server"), args[0] is "enroll" ? ReadEnrollmentCode() : null,
         options.GetValueOrDefault("--printer"), options.TryGetValue("--exposed", out string? choice) ? IsWord(choice, "true") : null);
      string response = await host.SendAsync(request);
      host.Output.WriteLine(response);
      bool failed = response.StartsWith("Error:", StringComparison.Ordinal);
      if(args[0] is "sign-out" && !failed)
      {
         host.Output.WriteLine("The identity provider's browser session is not ended from the command line; use Sign out in the QQQ Print Agent app to end it too.");
      }
      return failed ? 1 : 0;
   }



   /// <summary>
   /// Says whether the command line is a request for help: no arguments, a help word in first place whatever follows
   /// ("help enroll" is a person asking how to enroll), or a help flag in last place after a real command
   /// ("printers --help"). Answering costs nothing and sends nothing.
   /// </summary>
   private static bool AsksForHelp(string[] args)
      => args is [] or ["help" or "--help" or "-h", ..] || (s_commands.ContainsKey(args[0]) && args[^1] is "--help" or "-h");



   /// <summary>
   /// Refuses a command line: the reason and the help text go to standard error and the exit code is 1. The reason never
   /// repeats what was typed. A refused command line can hold a secret in the wrong place (an enrollment code given as an
   /// argument, a sign-in callback address), and standard error is routinely captured into logs.
   /// </summary>
   private int RefuseArguments(string reason)
   {
      host.Error.WriteLine(reason);
      host.Error.WriteLine(Usage);
      return 1;
   }



   /// <summary>
   /// Checks the command line and reads the "--name value" pairs that follow the command, in any order, into the given
   /// dictionary. It returns null when the command line is acceptable, and otherwise the reason it is not, which the
   /// caller refuses with rather than guess what was meant. Every option of a command is required exactly once, with a
   /// value that is not blank and is not itself an option name, so a forgotten value cannot swallow the next option.
   /// Each reason is assembled only from this class's own command and option names, never from an argument that did not
   /// match one of them, so it cannot repeat a secret that was typed in the wrong place.
   /// </summary>
   private static string? FindProblem(string[] args, Dictionary<string, string> options)
   {
      if(args.Any(s_removedOptions.Contains))
      {
         return RemovedOptionsReason;
      }
      if(!s_commands.TryGetValue(args[0], out (string Operation, string[] Options) command))
      {
         return "Unknown command.";
      }
      for(int index = 1; index < args.Length; index += 2)
      {
         string name = args[index];
         if(!command.Options.Contains(name, StringComparer.Ordinal))
         {
            return command.Options.Length == 0 ? $"'{args[0]}' takes no arguments." : $"'{args[0]}' accepts only {string.Join(" and ", command.Options)}.";
         }
         if(options.ContainsKey(name))
         {
            return $"{name} may be given only once.";
         }
         if(index + 1 == args.Length || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
         {
            return $"{name} needs a value.";
         }
         options[name] = args[index + 1];
      }
      string? missing = command.Options.FirstOrDefault(option => !options.ContainsKey(option));
      if(missing is not null)
      {
         return $"{missing} is required.";
      }
      return options.TryGetValue("--exposed", out string? choice) && !IsWord(choice, "true") && !IsWord(choice, "false") ? "--exposed must be true or false." : null;
   }



   /// <summary>Compares an option value with a fixed word, ignoring letter case and nothing else: "TRUE" is true, " true" is not.</summary>
   private static bool IsWord(string text, string word) => string.Equals(text, word, StringComparison.OrdinalIgnoreCase);



   /// <summary>
   /// Reads the one-time enrollment code from the environment, or else from standard input, so the code never appears in
   /// the command line or shell history. It is passed to the service and nowhere else: this class never prints it. With
   /// the variable or redirected input nothing is written, so standard error stays empty unless something fails.
   /// Surrounding white space is dropped, because a code never has any and a shell can add it (cmd's "echo CODE | ..."
   /// sends a trailing space).
   /// </summary>
   private string ReadEnrollmentCode()
   {
      string? code = host.ReadEnvironmentVariable(EnrollmentCodeVariable);
      if(string.IsNullOrWhiteSpace(code))
      {
         //////////////////////////////////////////////////////////////////////////////
         // redirected input is read silently. a prompt would be the only thing on   //
         // standard error in a successful scripted enrollment, and Windows          //
         // PowerShell turns any standard error output of a native program into an   //
         // error, which with ErrorActionPreference Stop ends the script after the   //
         // single-use code has already been spent                                   //
         //////////////////////////////////////////////////////////////////////////////
         code = host.IsInputRedirected() ? host.ReadLine() : PromptWithoutEcho();
      }
      return !string.IsNullOrWhiteSpace(code) ? code.Trim() : throw new InvalidOperationException("Enrollment code is required.");
   }



   /// <summary>
   /// Prompts at the console and reads one line key by key without showing it, so a typed code is not left on the screen
   /// or in a terminal recording. The prompt goes to standard error, so it appears even when standard output is captured.
   /// Enter ends the line, and so does a key that carries a carriage return or line feed without being the Enter key
   /// (Ctrl+M, or a line end pasted by a terminal). Backspace removes the last character typed, and keys that produce no
   /// printable character (arrows, function keys) are ignored. The line end that ended the read was not shown either, so
   /// the prompt line is ended here, also when reading fails, before anything else is written.
   /// </summary>
   private string PromptWithoutEcho()
   {
      host.Error.Write("Enrollment code: ");
      try
      {
         StringBuilder typed = new();
         for(ConsoleKeyInfo key = host.ReadKey(true); key.Key != ConsoleKey.Enter && key.KeyChar is not ('\r' or '\n'); key = host.ReadKey(true))
         {
            if(key.Key == ConsoleKey.Backspace)
            {
               typed.Length = Math.Max(0, typed.Length - 1);
            }
            else if(!char.IsControl(key.KeyChar))
            {
               typed.Append(key.KeyChar);
            }
         }
         return typed.ToString();
      }
      finally
      {
         host.Error.WriteLine();
      }
   }
}
