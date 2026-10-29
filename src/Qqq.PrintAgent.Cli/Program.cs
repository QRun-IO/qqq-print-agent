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
   public static async Task<int> Main(string[] args)
   {
      try
      {
         string command = args.FirstOrDefault() ?? "help";
         string directory = Option(args, "--data-dir") ?? AgentStore.DefaultDirectory;
         switch(command)
         {
            case "printers":
               Console.WriteLine(JsonSerializer.Serialize(new WindowsPrinters().Discover(), PrintApiClient.JsonOptions));
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
      catch(OperationCanceledException) { return 0; }
      catch(Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
   }



   private static string? Option(string[] args, string key)
   {
      int index = Array.IndexOf(args, key);
      return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
   }



   private static async Task<int> RunAsync(string[] args, string directory)
   {
      Directory.CreateDirectory(directory);
      using FileStream instance = new(Path.Combine(directory, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
      using AgentStore journal = new(directory);
      AgentConfiguration config = journal.LoadConfiguration() ?? throw new InvalidOperationException("Enroll this account first.");
      using PrintApiClient client = new(config.ServerUrl, config.DeviceToken);
      ExposedPrinterBackend printers = new(new WindowsPrinters(), journal.GetExposedPrinterIds);
      PrintWorker worker = new(client, printers, journal, Console.WriteLine);
      using CancellationTokenSource stopping = new();
      Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
      do
      {
         await worker.RunOnceAsync(stopping.Token);
         if(args.Contains("--once", StringComparer.Ordinal))
         {
            break;
         }
         await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, config.PollIntervalSeconds)), stopping.Token);
      } while(!stopping.IsCancellationRequested);
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
      using AgentStore store = new(directory);
      store.SetExposure(id, exposed, new WindowsPrinters().Discover());
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
      using AgentStore store = new(directory);
      using PrintApiClient api = new(server);
      EnrollmentResponse result = await api.EnrollAsync(new EnrollmentRequest(code, Environment.MachineName, "0.1.0"));
      store.SaveConfiguration(new AgentConfiguration(server, result.DeviceId, result.DeviceToken, result.PollIntervalSeconds));
      Console.WriteLine($"Enrolled device {result.DeviceId}.");
      return 0;
   }
}

