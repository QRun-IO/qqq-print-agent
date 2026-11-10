/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli;

/// <summary>Scriptable management of the installed service: setup, printer selection and status.</summary>
public static class Program
{
   /// <summary>
   /// Entry point. Three argument shapes are internal and fixed: the isolated printer worker, the MSI update helper, and the
   /// browser sign-in callback forwarder. Everything else is an operator command.
   /// </summary>
   public static Task<int> Main(string[] args) => args switch
   {
      ["--printer-worker"] => PrinterWorkerHost.RunAsync(),
      ["--apply-update", string request] => ExecuteAsync(() => MsiUpdateInstaller.ApplyAsync(request)),
      ["--auth-callback", string callback] => ExecuteAsync(async () =>
      {
         await QqqBrowserCallback.ForwardFromHandlerAsync(callback);
         return 0;
      }),
      _ => ExecuteAsync(() => DispatchAsync(args)),
   };



   /// <summary>
   /// Runs a command and turns failures into a one-line message on standard error and exit code 1. A timeout or cancellation
   /// also exits 1, so a script never mistakes an operation that did not finish for one that succeeded.
   /// </summary>
   internal static async Task<int> ExecuteAsync(Func<Task<int>> command)
   {
      try
      {
         return await command();
      }
      catch(OperationCanceledException)
      {
         Console.Error.WriteLine("The operation timed out or was canceled before completion.");
         return 1;
      }
      catch(Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
   }



   /// <summary>
   /// Routes an operator command to the installed service over its authenticated pipe, because the service owns the
   /// signed-in session, the pairing and all printing. The command line has no state or server transport of its own. The
   /// options of the removed legacy mode are refused by name, so a script written for it fails loudly instead of running
   /// against the service by accident.
   /// </summary>
   internal static async Task<int> DispatchAsync(string[] args, Func<PipeRequest, Task<string>>? serviceCommand = null)
   {
      string command = args.FirstOrDefault() ?? "help";
      if(args.Any(argument => argument is "--legacy-compat" or "--data-dir" or "--once" or "--service"))
      {
         throw new ArgumentException("The --legacy-compat mode and its options (--data-dir, --once, --service) were removed in 2.0. The command line only manages the installed service.", nameof(args));
      }
      if(command is "printers" or "enroll" or "expose" or "run" or "sign-out")
      {
         return await DispatchServiceAsync(args, serviceCommand ?? AgentPipeClient.SendAsync);
      }
      switch(command)
      {
         case "service-status":
         case "snapshot":
            Console.WriteLine(await AgentPipeClient.SendAsync(new PipeRequest(command is "snapshot" ? "snapshot" : "status")));
            return 0;
         default:
            Console.WriteLine("printers | enroll --server URL | expose --printer ID --exposed true|false | run | sign-out | service-status | snapshot\nCommands use the authenticated installed service. Sign in through the companion first. run requests a connection refresh; the Windows service owns unattended printing.\nEnrollment code is read from QQQ_PRINT_ENROLLMENT_CODE or an interactive prompt.");
            return command is "help" ? 0 : 1;
      }
   }



   /// <summary>Sends a command to the installed service and prints its reply, exiting 1 when the reply is an error.</summary>
   private static async Task<int> DispatchServiceAsync(string[] args, Func<PipeRequest, Task<string>> send)
   {
      PipeRequest request = args[0] switch
      {
         "run" => new("reconnect"),
         "enroll" => new("enroll", Option(args, "--server") ?? throw new ArgumentException("--server is required.", nameof(args)), ReadEnrollmentCode()),
         "expose" => new("set-exposure", PrinterId: Option(args, "--printer") ?? throw new ArgumentException("--printer is required.", nameof(args)),
            Exposed: bool.TryParse(Option(args, "--exposed"), out bool exposed) ? exposed : throw new ArgumentException("--exposed must be true or false.", nameof(args))),
         _ => new(args[0]),
      };
      string response = await send(request);
      Console.WriteLine(response);
      if(args[0] is "sign-out" && !response.StartsWith("Error:", StringComparison.Ordinal))
      {
         Console.WriteLine("The identity provider's browser session is not ended from the command line; use Sign out in the QQQ Print Agent app to end it too.");
      }
      return response.StartsWith("Error:", StringComparison.Ordinal) ? 1 : 0;
   }



   /// <summary>Reads the one-time enrollment code from the environment, or prompts for it, so the code never appears in the command line or shell history.</summary>
   private static string ReadEnrollmentCode()
   {
      string? code = Environment.GetEnvironmentVariable("QQQ_PRINT_ENROLLMENT_CODE");
      if(string.IsNullOrWhiteSpace(code))
      {
         Console.Write("Enrollment code: ");
         code = Console.ReadLine();
      }
      return !string.IsNullOrWhiteSpace(code) ? code : throw new InvalidOperationException("Enrollment code is required.");
   }



   /// <summary>Returns the value following a named option, or null when the option is absent or has no value.</summary>
   private static string? Option(string[] args, string key)
   {
      int index = Array.IndexOf(args, key);
      return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
   }
}
