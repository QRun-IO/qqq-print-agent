/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The built qqq-print-agent.exe, started as a real process. The other command-line tests drive the logic against fakes;
/// these prove the shipped entry point is wired to it: real exit codes, real standard output and standard error, and the
/// printer-worker role still answering its parent. None of them needs the installed service, and none reaches a printer:
/// the worker is only ever given a request it must refuse.
/// </summary>
public sealed class CliProcessTests
{
   private const string ParentVariable = "QQQ_PRINT_WORKER_PARENT";



   /// <summary>The executable built beside this test assembly.</summary>
   private static string Executable => Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");



   /// <summary>Asking the real executable for help exits 0 with the usage on standard output and nothing on standard error.</summary>
   [Theory]
   [InlineData("--help")]
   [InlineData("-h")]
   [InlineData("help")]
   public async Task Help_ExitsZeroWithUsageOnStandardOutput(string argument)
   {
      (int exitCode, string output, string error) = await RunAsync(parentProcessId: null, input: null, argument);

      Assert.Equal(0, exitCode);
      Assert.Equal(CommandLine.Usage.ReplaceLineEndings("\n") + "\n", output);
      Assert.Equal("", error);
   }



   /// <summary>With no arguments at all the real executable also prints the usage and exits 0.</summary>
   [Fact]
   public async Task NoArguments_ExitsZeroWithUsageOnStandardOutput()
   {
      (int exitCode, string output, string error) = await RunAsync(parentProcessId: null, input: null);

      Assert.Equal(0, exitCode);
      Assert.Equal(CommandLine.Usage.ReplaceLineEndings("\n") + "\n", output);
      Assert.Equal("", error);
   }



   /// <summary>An unknown command, here the removed legacy flag, makes the real executable exit 1 with the reason and the usage on standard error and nothing on standard output.</summary>
   [Fact]
   public async Task UnknownCommand_ExitsOneWithUsageOnStandardError()
   {
      (int exitCode, string output, string error) = await RunAsync(parentProcessId: null, input: null, "--legacy-compat", "run", "--once");

      Assert.Equal(1, exitCode);
      Assert.Equal("", output);
      Assert.Equal("Unknown command.\n" + CommandLine.Usage.ReplaceLineEndings("\n") + "\n", error);
   }



   /// <summary>Started as a printer worker without a parent to watch, the real executable refuses in the worker's own protocol: one JSON line on standard output reporting failure, exit code 1, and nothing on standard error. That refusal comes from the worker host, which shows the role is still reached through this executable.</summary>
   [Fact]
   public async Task PrinterWorker_WithoutAParent_RefusesInTheWorkerProtocol()
   {
      (int exitCode, string output, string error) = await RunAsync(parentProcessId: null, input: "{\"operation\":\"discover\"}", "--printer-worker");

      Assert.Equal(1, exitCode);
      using var answer = JsonDocument.Parse(output);
      Assert.False(answer.RootElement.GetProperty("success").GetBoolean());
      Assert.Contains("launched by the print connector", answer.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
      Assert.Equal("", error);
   }



   /// <summary>Started as a printer worker by a live parent, the real executable reads one request from standard input and answers it on standard output. The request names an operation that does not exist, so the worker refuses it without touching any printer.</summary>
   [Fact]
   public async Task PrinterWorker_WithAParent_ReadsARequestAndAnswersIt()
   {
      (int exitCode, string output, string error) = await RunAsync(Environment.ProcessId, "{\"operation\":\"synthetic-operation\"}", "--printer-worker");

      Assert.Equal(1, exitCode);
      using var answer = JsonDocument.Parse(output);
      Assert.False(answer.RootElement.GetProperty("success").GetBoolean());
      Assert.Equal("Unknown printer worker operation.", answer.RootElement.GetProperty("error").GetString());
      Assert.Equal("", error);
   }



   /// <summary>
   /// Starts the executable with the given arguments and waits for it to end, returning its exit code and what it wrote, with
   /// line endings normalized. The worker's parent variable is set only when a parent is given, standard input carries the
   /// given line and is then closed, and a process that has not finished within a minute is killed so a hang fails the test
   /// instead of blocking the run.
   /// </summary>
   private static async Task<(int ExitCode, string Output, string Error)> RunAsync(int? parentProcessId, string? input, params string[] args)
   {
      ProcessStartInfo start = new(Executable)
      {
         UseShellExecute = false,
         CreateNoWindow = true,
         RedirectStandardInput = true,
         RedirectStandardOutput = true,
         RedirectStandardError = true,
      };
      foreach(string argument in args)
      {
         start.ArgumentList.Add(argument);
      }
      start.Environment.Remove(ParentVariable);
      if(parentProcessId is int parent)
      {
         start.Environment[ParentVariable] = parent.ToString(CultureInfo.InvariantCulture);
      }
      using Process process = Process.Start(start) ?? throw new InvalidOperationException("The command-line executable did not start.");
      using CancellationTokenSource limit = new(TimeSpan.FromMinutes(1));
      try
      {
         Task<string> output = process.StandardOutput.ReadToEndAsync(limit.Token);
         Task<string> error = process.StandardError.ReadToEndAsync(limit.Token);
         if(input is not null)
         {
            await process.StandardInput.WriteLineAsync(input.AsMemory(), limit.Token);
         }
         process.StandardInput.Close();
         await process.WaitForExitAsync(limit.Token);
         return (process.ExitCode, (await output).ReplaceLineEndings("\n"), (await error).ReplaceLineEndings("\n"));
      }
      finally
      {
         if(!process.HasExited)
         {
            process.Kill(entireProcessTree: true);
         }
      }
   }
}
