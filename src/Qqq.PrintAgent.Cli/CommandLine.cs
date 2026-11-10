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
        help                                      Show this text (also --help, -h).

      Every command is carried out by the installed print service, which owns the sign-in, the pairing and all printing.
      Run from an administrator prompt, and sign in through the companion app first.
      The enrollment code is read from the QQQ_PRINT_ENROLLMENT_CODE environment variable, or else from standard input:
      typed at a prompt, where it is not shown, or piped in. It is never accepted on the command line.
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
   /// one pipe request and sent to the service. The reply is printed as the service wrote it. A reply that starts with
   /// "Error:" is the service refusing or failing the request, so it exits 1 for every command.
   /// </summary>
   private async Task<int> ManageServiceAsync(string[] args)
   {
      ///////////////////////////////////////////////////////////////////////////
      // help is decided by the first word alone: "help enroll" is a person    //
      // asking how to enroll, and answering costs nothing and sends nothing   //
      ///////////////////////////////////////////////////////////////////////////
      if(args is [] or ["help" or "--help" or "-h", ..])
      {
         host.Output.WriteLine(Usage);
         return 0;
      }
      if(!s_commands.TryGetValue(args[0], out (string Operation, string[] Options) command))
      {
         return RefuseArguments("Unknown command.");
      }
      Dictionary<string, string>? options = ReadOptions(args, command.Options);
      if(options is null)
      {
         return RefuseArguments($"Invalid arguments for '{args[0]}'.");
      }
      bool? exposed = null;
      if(options.TryGetValue("--exposed", out string? text))
      {
         if(!bool.TryParse(text, out bool parsed))
         {
            return RefuseArguments("--exposed must be true or false.");
         }
         exposed = parsed;
      }

      ////////////////////////////////////////////////////////////////////////////
      // the enrollment code is asked for last, once the command line is known  //
      // to be valid, so a mistyped command never prompts for a single-use code //
      ////////////////////////////////////////////////////////////////////////////
      PipeRequest request = new(command.Operation, options.GetValueOrDefault("--server"), args[0] is "enroll" ? ReadEnrollmentCode() : null,
         options.GetValueOrDefault("--printer"), exposed);
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
   /// Reads the "--name value" pairs that follow a command, in any order. Every option of a command is required exactly
   /// once, so the argument count must match, each name must be one of the command's own, no name may repeat and no value
   /// may be blank. Anything else returns null, and the caller refuses the whole command line rather than guess.
   /// </summary>
   private static Dictionary<string, string>? ReadOptions(string[] args, string[] names)
   {
      if(args.Length != 1 + (2 * names.Length))
      {
         return null;
      }
      Dictionary<string, string> values = new(StringComparer.Ordinal);
      for(int index = 1; index < args.Length; index += 2)
      {
         if(!names.Contains(args[index], StringComparer.Ordinal) || string.IsNullOrWhiteSpace(args[index + 1]) || !values.TryAdd(args[index], args[index + 1]))
         {
            return null;
         }
      }
      return values;
   }



   /// <summary>
   /// Reads the one-time enrollment code from the environment, or else from standard input, so the code never appears in
   /// the command line or shell history. It is passed to the service and nowhere else: this class never prints it.
   /// Surrounding white space is dropped, because a code never has any and a shell can add it (cmd's "echo CODE | ..."
   /// sends a trailing space).
   /// </summary>
   private string ReadEnrollmentCode()
   {
      string? code = host.ReadEnvironmentVariable(EnrollmentCodeVariable);
      if(string.IsNullOrWhiteSpace(code))
      {
         ///////////////////////////////////////////////////////////////////////////
         // the prompt goes to standard error, so a script that pipes the code in //
         // and captures standard output gets the service's reply and nothing     //
         // else. neither way of reading echoes the line end, so the prompt line  //
         // is ended here, before the reply or an error message is written        //
         ///////////////////////////////////////////////////////////////////////////
         host.Error.Write("Enrollment code: ");
         code = host.IsInputRedirected() ? host.ReadLine() : ReadWithoutEcho();
         host.Error.WriteLine();
      }
      return !string.IsNullOrWhiteSpace(code) ? code.Trim() : throw new InvalidOperationException("Enrollment code is required.");
   }



   /// <summary>
   /// Reads one line from the console key by key without showing it, so a code typed at the prompt is not left on the
   /// screen or in a terminal recording. Enter ends the line, Backspace removes the last character typed, and keys that
   /// produce no character (arrows, function keys) are ignored.
   /// </summary>
   private string ReadWithoutEcho()
   {
      StringBuilder typed = new();
      for(ConsoleKeyInfo key = host.ReadKey(true); key.Key != ConsoleKey.Enter; key = host.ReadKey(true))
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
}
