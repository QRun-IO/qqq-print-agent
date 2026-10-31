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

   internal static bool UseLegacyCompatibility(string[] args)
   {
      bool legacy = args.Contains("--legacy-compat", StringComparer.Ordinal);
      if(legacy && args.Contains("--service", StringComparer.Ordinal))
      {
         throw new ArgumentException("Legacy compatibility cannot access the installed service's state.", nameof(args));
      }
      return legacy;
   }

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



   private static string? Option(string[] args, string key)
   {
      int index = Array.IndexOf(args, key);
      return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
   }

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

   internal static FileStream AcquireWorkerLock(string directory)
   {
      Directory.CreateDirectory(directory);
      return new FileStream(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
   }

   private static IsolatedPrinterBackend CreatePrinterBackend() => new(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the printer helper executable."));



   private static async Task<int> RunAsync(string[] args, string directory)
   {
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
      {
         do
         {
            await worker.RunOnceAsync(stopping.Token);
            if(args.Contains("--once", StringComparer.Ordinal))
            {
               break;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.PollIntervalSeconds)), stopping.Token);
         } while(!stopping.IsCancellationRequested);
      }
      catch(OperationCanceledException) when(stopping.IsCancellationRequested) { return 0; }
      finally { Console.CancelKeyPress -= cancelHandler; }
      return 0;
   }



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

