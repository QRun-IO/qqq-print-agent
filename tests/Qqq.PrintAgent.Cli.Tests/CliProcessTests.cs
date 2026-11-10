/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The built qqq-print-agent.exe, started as a real process. The other command-line tests drive the logic against fakes;
/// these prove the shipped entry point is wired to it: real exit codes, real standard output and standard error. None of
/// them needs the installed service: every command line here is answered or refused before the service would be contacted.
/// </summary>
public sealed class CliProcessTests
{
   /// <summary>The executable built beside this test assembly.</summary>
   private static string Executable => Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");



   /// <summary>Asking the real executable for help exits 0 with the usage on standard output and nothing on standard error.</summary>
   [Theory]
   [InlineData("--help")]
   [InlineData("-h")]
   [InlineData("help")]
   public async Task Help_ExitsZeroWithUsageOnStandardOutput(string argument)
   {
      (int exitCode, string output, string error) = await RunAsync(input: null, argument);

      Assert.Equal(0, exitCode);
      Assert.Equal(CommandLine.Usage.ReplaceLineEndings("\n") + "\n", output);
      Assert.Equal("", error);
   }



   /// <summary>With no arguments at all the real executable also prints the usage and exits 0.</summary>
   [Fact]
   public async Task NoArguments_ExitsZeroWithUsageOnStandardOutput()
   {
      (int exitCode, string output, string error) = await RunAsync(input: null);

      Assert.Equal(0, exitCode);
      Assert.Equal(CommandLine.Usage.ReplaceLineEndings("\n") + "\n", output);
      Assert.Equal("", error);
   }



   /// <summary>An unknown command makes the real executable exit 1 with the reason and the usage on standard error and nothing on standard output.</summary>
   [Fact]
   public async Task UnknownCommand_ExitsOneWithUsageOnStandardError()
   {
      (int exitCode, string output, string error) = await RunAsync(input: null, "status");

      Assert.Equal(1, exitCode);
      Assert.Equal("", output);
      Assert.Equal("Unknown command.\n" + CommandLine.Usage.ReplaceLineEndings("\n") + "\n", error);
   }



   /// <summary>The real executable is not a printer worker. Given the worker's argument and a worker request on standard input, it answers nothing on standard output, where a parent would expect the worker's reply, and refuses the argument as an unknown command.</summary>
   [Fact]
   public async Task PrinterWorkerArgument_IsAnUnknownCommand()
   {
      (int exitCode, string output, string error) = await RunAsync("{\"operation\":\"discover\"}", "--printer-worker");

      Assert.Equal(1, exitCode);
      Assert.Equal("", output);
      Assert.Equal("Unknown command.\n" + CommandLine.Usage.ReplaceLineEndings("\n") + "\n", error);
   }



   /// <summary>
   /// Starts the executable with the given arguments and waits for it to end, returning its exit code and what it wrote, with
   /// line endings normalized. Standard input carries the given line, if any, and is then closed. The enrollment code
   /// variable is removed from the child's environment, so the result does not depend on the machine running the test. A
   /// process that has not finished within a minute is killed, so a hang fails the test instead of blocking the run.
   /// </summary>
   private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string? input, params string[] args)
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
      start.Environment.Remove(CommandLine.EnrollmentCodeVariable);
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
