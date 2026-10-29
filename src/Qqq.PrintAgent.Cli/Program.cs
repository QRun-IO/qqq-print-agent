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
   public static Task<int> Main(string[] args) => args.Length == 1 && string.Equals(args[0], "--printer-worker", StringComparison.Ordinal)
      ? PrinterWorkerHost.RunAsync()
      : ExecuteAsync(() => DispatchAsync(args));

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

   private static async Task<int> DispatchAsync(string[] args)
   {
      string command = args.FirstOrDefault() ?? "help";
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
            Console.WriteLine("printers | enroll --server URL [--data-dir PATH] [--service] | expose --printer ID --exposed true|false [--data-dir PATH] [--service] | run [--data-dir PATH] [--once] | service-status | snapshot\nEnrollment code is read from QQQ_PRINT_ENROLLMENT_CODE or an interactive prompt.");
            return command is "help" ? 0 : 1;
      }
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

