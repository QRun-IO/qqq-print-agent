/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli;

/// <summary>Scriptable setup, printer selection and foreground diagnostics.</summary>
public static class Program
{
   /// <summary>
   /// Entry point. Three argument shapes are internal and fixed: the isolated printer worker (a child process the service
   /// starts for each native printer call), the MSI update helper, and the browser sign-in callback forwarder. Everything
   /// else is an operator command.
   /// </summary>
   public static Task<int> Main(string[] args) => args switch
   {
      ["--printer-worker"] => PrinterWorkerHost.RunAsync(),
      ["--apply-update", string request] => ExecuteAsync(() => MsiUpdateInstaller.ApplyAsync(request)),
      ["--auth-callback", string callback] => ExecuteAsync(async () =>
      {
         await QqqBrowserCallback.ForwardAsync(callback);
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
   /// Routes an operator command. Without the explicit legacy flag, printing commands go to the installed service over its
   /// authenticated pipe, because the service owns the signed-in session; only with the flag do they use a separate
   /// per-user data directory and the old device-token transport, and that is announced on every run.
   /// </summary>
   internal static async Task<int> DispatchAsync(string[] args, Func<PipeRequest, Task<string>>? serviceCommand = null)
   {
      string command = args.FirstOrDefault() ?? "help";
      bool legacy = UseLegacyCompatibility(args);
      if(legacy)
      {
         Console.Error.WriteLine("Explicit legacy compatibility diagnostics: private device bearer transport, separate local state. This is not QQQ user sign-in.");
      }
      if(!legacy && command is "printers" or "enroll" or "expose" or "run" or "sign-out")
      {
         return await DispatchServiceAsync(args, serviceCommand ?? AgentPipeClient.SendAsync);
      }
      string directory = ResolveDataDirectory(args);
      switch(command)
      {
         case "printers":
            Console.WriteLine(JsonSerializer.Serialize(CreatePrinterBackend().Discover(), PrintApiClient.JsonOptions));
            return 0;
         case "service-status":
         case "snapshot":
            Console.WriteLine(await AgentPipeClient.SendAsync(new PipeRequest(command is "snapshot" ? "snapshot" : "status")));
            return 0;
         case "enroll":
            return await EnrollAsync(args, directory);
         case "expose":
            return await SetExposureAsync(args, directory);
         case "run":
            return await RunAsync(args, directory);
         default:
            Console.WriteLine("printers | enroll --server URL | expose --printer ID --exposed true|false | run | sign-out | service-status | snapshot\nNormal commands use the authenticated installed service. Sign in through the companion first. run requests a connection refresh; the Windows service owns unattended printing.\nExplicit isolated legacy diagnostics only: add --legacy-compat and a separate --data-dir; legacy run accepts --once. Enrollment code is read from QQQ_PRINT_ENROLLMENT_CODE or an interactive prompt.");
            return command is "help" ? 0 : 1;
      }
   }



   /// <summary>Reports whether the legacy flag was given, and refuses it combined with --service, because legacy mode must never touch the installed service's state.</summary>
   internal static bool UseLegacyCompatibility(string[] args)
   {
      bool legacy = args.Contains("--legacy-compat", StringComparer.Ordinal);
      if(legacy && args.Contains("--service", StringComparer.Ordinal))
      {
         throw new ArgumentException("Legacy compatibility cannot access the installed service's state.", nameof(args));
      }
      return legacy;
   }



   /// <summary>
   /// Sends a normal command to the installed service and prints its reply, exiting 1 when the reply is an error. Options
   /// that only make sense for the isolated legacy mode (a data directory, run once) are refused here with an explanation,
   /// so a script cannot silently run against the wrong state.
   /// </summary>
   private static async Task<int> DispatchServiceAsync(string[] args, Func<PipeRequest, Task<string>> send)
   {
      if(args.Contains("--data-dir", StringComparer.Ordinal) || args.Contains("--once", StringComparer.Ordinal))
      {
         throw new ArgumentException("Foreground diagnostic options require explicit --legacy-compat. Normal printing is owned by the signed-in Windows service.", nameof(args));
      }
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



   /// <summary>
   /// Chooses the data directory. --service selects the installed machine directory; otherwise a per-user directory is used,
   /// and the machine directory is refused there, so a foreground command can never overwrite the service's credentials by accident.
   /// </summary>
   internal static string ResolveDataDirectory(string[] args)
   {
      if(args.Contains("--service", StringComparer.Ordinal))
      {
         return AgentStore.DefaultDirectory;
      }
      string directory = Path.GetFullPath(Option(args, "--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QqqPrintAgent"));
      if(string.Equals(Path.TrimEndingDirectorySeparator(directory), Path.TrimEndingDirectorySeparator(AgentStore.DefaultDirectory), StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidOperationException("Use --service to manage the installed service. Foreground commands require a separate per-user data directory.");
      }
      return directory;
   }



   /// <summary>Takes the exclusive lock on a data directory so two foreground commands (or one beside the service) cannot write the same journal at once.</summary>
   internal static FileStream AcquireWorkerLock(string directory)
   {
      Directory.CreateDirectory(directory);
      return new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
   }



   /// <summary>Creates the isolated printer backend that runs each native call in a child copy of this executable, bounding any blocking driver call.</summary>
   private static IsolatedPrinterBackend CreatePrinterBackend() => new(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the printer helper executable."));



   /// <summary>
   /// Runs one legacy diagnostic claim pass. Continuous legacy polling is gone, so only --once is accepted. Ctrl+C ends the
   /// pass cleanly (exit 0); any other cancellation is a failure. It uses a separate data directory and the device token.
   /// </summary>
   private static async Task<int> RunAsync(string[] args, string directory)
   {
      if(!args.Contains("--once", StringComparer.Ordinal))
      {
         throw new InvalidOperationException("Continuous legacy polling is no longer supported. Sign in to the installed native-session service for realtime printing. --once is a legacy diagnostic only.");
      }
      using FileStream instance = AcquireWorkerLock(directory);
      using AgentStore journal = new(directory);
      AgentConfiguration config = journal.LoadConfiguration() ?? throw new InvalidOperationException("Enroll this account first.");
      using PrintApiClient client = new(config.ServerUrl, config.DeviceToken);
      ExposedPrinterBackend printers = new(CreatePrinterBackend(), journal.GetExposedPrinterIds);
      PrintWorker worker = new(client, printers, journal, Console.WriteLine);
      using CancellationTokenSource stopping = new();
      ConsoleCancelEventHandler cancelHandler = (_, e) => { e.Cancel = true; stopping.Cancel(); };
      Console.CancelKeyPress += cancelHandler;
      try
      { await worker.RunOnceAsync(stopping.Token); }
      catch(OperationCanceledException) when(stopping.IsCancellationRequested) { return 0; }
      finally { Console.CancelKeyPress -= cancelHandler; }
      return 0;
   }



   /// <summary>Shares or hides one printer, through the service when --service is given and otherwise in the separate legacy directory after validating against current discovery.</summary>
   private static async Task<int> SetExposureAsync(string[] args, string directory)
   {
      string id = Option(args, "--printer") ?? throw new ArgumentException("--printer is required.", nameof(args));
      if(!bool.TryParse(Option(args, "--exposed"), out bool exposed))
      {
         throw new ArgumentException("--exposed must be true or false.", nameof(args));
      }
      if(args.Contains("--service", StringComparer.Ordinal))
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("set-exposure", PrinterId: id, Exposed: exposed));
         Console.WriteLine(response);
         return response.StartsWith("Error:", StringComparison.Ordinal) ? 1 : 0;
      }
      using FileStream instance = AcquireWorkerLock(directory);
      using AgentStore store = new(directory);
      store.SetExposure(id, exposed, CreatePrinterBackend().Discover());
      Console.WriteLine(exposed ? "Printer exposed to QQQ." : "Printer hidden from QQQ.");
      return 0;
   }



   /// <summary>
   /// Enrolls this computer with a one-time code read from the environment or a prompt, never from the command line. With
   /// --service the installed service does it; otherwise the legacy path enrolls into a separate directory and saves the
   /// device connection protected for the current user.
   /// </summary>
   private static async Task<int> EnrollAsync(string[] args, string directory)
   {
      string server = Option(args, "--server") ?? throw new ArgumentException("--server is required.", nameof(args));
      string? code = Environment.GetEnvironmentVariable("QQQ_PRINT_ENROLLMENT_CODE");
      if(string.IsNullOrWhiteSpace(code))
      {
         Console.Write("Enrollment code: ");
         code = Console.ReadLine();
      }
      if(string.IsNullOrWhiteSpace(code))
      {
         throw new InvalidOperationException("Enrollment code is required.");
      }
      if(args.Contains("--service", StringComparer.Ordinal))
      {
         string response = await AgentPipeClient.SendAsync(new PipeRequest("enroll", server, code));
         Console.WriteLine(response);
         return response.StartsWith("Error:", StringComparison.Ordinal) ? 1 : 0;
      }
      using FileStream instance = AcquireWorkerLock(directory);
      using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
      using PrintApiClient api = new(server);
      EnrollmentResponse result = await api.EnrollAsync(new EnrollmentRequest(code, Environment.MachineName, "0.1.0"));
      store.SaveConfiguration(new AgentConfiguration(server, result.DeviceId, result.DeviceToken, result.PollIntervalSeconds));
      Console.WriteLine($"Enrolled device {result.DeviceId}.");
      return 0;
   }
}
