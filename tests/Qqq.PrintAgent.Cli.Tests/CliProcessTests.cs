/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The built qqq-print-agent.exe, started as a real process. The other command-line tests drive the logic against fakes;
/// these prove the shipped entry point is wired to it: real exit codes, real standard output and standard error. None of
/// them needs the installed service: every command line here is answered or refused before the service would be contacted.
/// </summary>
public sealed class CliProcessTests
{
   private const string NoCode = "Enrollment code is required.";
   private const string TooLarge = "Local pipe message exceeds the permitted size.";

   /// <summary>A synthetic server address that no request can carry to the service: each of its 11,000 ampersands is six characters in JSON, which is more than the pipe accepts.</summary>
   private static readonly string s_unsendableServer = "https://qqq.example.test/?" + new string('&', 11_000);



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
      Assert.Equal(CommandLine.Usage + Environment.NewLine, output);
      Assert.Equal("", error);
   }



   /// <summary>Every line of the usage the real executable writes, to either stream, ends the way this platform ends lines. The help text is a multi-line literal whose line ends follow the source checkout, so without care a Windows console would get bare line feeds with one carriage return at the very end.</summary>
   [Theory]
   [InlineData("--help")]
   [InlineData("status")]
   public async Task Usage_IsWrittenWithThePlatformLineEnds(string argument)
   {
      (_, string output, string error) = await RunAsync(input: null, argument);

      string written = output + error;
      Assert.True(written.Split(Environment.NewLine).Length > 15, "The usage should span many lines.");
      string withoutLineEnds = written.Replace(Environment.NewLine, "", StringComparison.Ordinal);
      Assert.DoesNotContain('\n', withoutLineEnds);
      Assert.DoesNotContain('\r', withoutLineEnds);
   }



   /// <summary>With no arguments at all the real executable also prints the usage and exits 0.</summary>
   [Fact]
   public async Task NoArguments_ExitsZeroWithUsageOnStandardOutput()
   {
      (int exitCode, string output, string error) = await RunAsync(input: null);

      Assert.Equal(0, exitCode);
      Assert.Equal(CommandLine.Usage + Environment.NewLine, output);
      Assert.Equal("", error);
   }



   /// <summary>An unknown command makes the real executable exit 1 with the reason and the usage on standard error and nothing on standard output.</summary>
   [Fact]
   public async Task UnknownCommand_ExitsOneWithUsageOnStandardError()
   {
      (int exitCode, string output, string error) = await RunAsync(input: null, "status");

      Assert.Equal(1, exitCode);
      Assert.Equal("", output);
      Assert.Equal("Unknown command." + Environment.NewLine + CommandLine.Usage + Environment.NewLine, error);
   }



   /// <summary>The real executable is not a printer worker. Given the worker's argument and a worker request on standard input, it answers nothing on standard output, where a parent would expect the worker's reply, and refuses the argument as an unknown command.</summary>
   [Fact]
   public async Task PrinterWorkerArgument_IsAnUnknownCommand()
   {
      (int exitCode, string output, string error) = await RunAsync("{\"operation\":\"discover\"}", "--printer-worker");

      Assert.Equal(1, exitCode);
      Assert.Equal("", output);
      Assert.Equal("Unknown command." + Environment.NewLine + CommandLine.Usage + Environment.NewLine, error);
   }



   /// <summary>The real executable, with no enrollment code in its environment and standard input closed, sees that input is redirected, reads it as a line without prompting, finds none, and stops there: the reason is the only thing on standard error, standard output is empty and the exit code is 1. It fails before any request would be sent, so the service is not contacted. A binding that took redirected input for a console would prompt and then fail reading a key, which this output excludes.</summary>
   [Fact]
   public async Task Enroll_WithNoCodeAndClosedInput_FailsBeforeContactingTheService()
   {
      (int exitCode, string output, string error) = await RunAsync(input: null, "enroll", "--server", "https://qqq.example.test");

      Assert.Equal(1, exitCode);
      Assert.Equal("", output);
      Assert.Equal("Enrollment code is required." + Environment.NewLine, error);
   }



   /// <summary>
   /// The real executable reads piped standard input as bytes and decodes them as UTF-8, or as a byte-order mark at the
   /// very start says, whatever the console input code page is. A child started here has a console of its own on the
   /// computer's OEM code page, which is where reading through the console's text reader went wrong: the three bytes
   /// of a UTF-8 mark became three other characters and were taken for a code. Each row is the bytes a shell or a
   /// file would deliver and what the executable then says. With a code in them it reaches the point of sending, and
   /// is stopped there by the size of the request (see <see cref="EnrollWithBytesAsync"/>): plain text, behind a UTF-8
   /// mark, as UTF-16 behind its mark, and with letters outside ASCII. With a mark and nothing after it, a mark and a
   /// line end (what Windows PowerShell 5.1 pipes for an empty string), two marks, or white space from outside ASCII
   /// that only UTF-8 makes white space, there is no code and it says so. Read in a code page, every row of the
   /// second kind but the blank line would hold some characters and end the first way. Either way nothing is
   /// prompted and nothing is written to standard output.
   /// </summary>
   [Theory]
   [InlineData("", "utf-8", "synthetic-code-4471\r\n", TooLarge)]
   [InlineData("EF BB BF", "utf-8", "synthetic-code-4471\r\n", TooLarge)]
   [InlineData("FF FE", "utf-16", "synthetic-code-4471\r\n", TooLarge)]
   [InlineData("", "utf-8", "synthétique-код-4471\r\n", TooLarge)]
   [InlineData("", "utf-8", "\r\n", NoCode)]
   [InlineData("EF BB BF", "utf-8", "", NoCode)]
   [InlineData("EF BB BF", "utf-8", "\r\n", NoCode)]
   [InlineData("EF BB BF EF BB BF", "utf-8", "\r\n", NoCode)]
   [InlineData("FF FE", "utf-16", "", NoCode)]
   [InlineData("FF FE", "utf-16", " \r\n", NoCode)]
   [InlineData("", "utf-8", "　 \r\n", NoCode)]
   [InlineData("EF BB BF", "utf-8", "　 \r\n", NoCode)]
   public async Task Enroll_ReadsPipedBytesAsUtf8OrAsTheirMarkSays(string mark, string encoding, string text, string said)
   {
      (int exitCode, string output, string error) = await EnrollWithBytesAsync(CliFixture.Bytes(mark, encoding, text));

      Assert.Equal(1, exitCode);
      Assert.Equal("", output);
      Assert.Equal(said + Environment.NewLine, error);
   }



   /// <summary>
   /// Executes the real binding for "is standard input redirected" in this process, and nothing more: it compares the
   /// binding with the property it should read, so it would not notice most wrong bindings. What proves the two branches
   /// is elsewhere. The redirected branch is proved by Enroll_WithNoCodeAndClosedInput_FailsBeforeContactingTheService,
   /// which runs the real executable with redirected input. The console branch is proved against fake keys in
   /// CliEnrollmentCodeTests; no test here has a real console to type at.
   /// </summary>
   [Fact]
   public void InputRedirectionBinding_ReportsTheConsoleState()
   {
      Assert.Equal(Console.IsInputRedirected, Program.IsInputRedirected());
   }



   /// <summary>
   /// Runs "enroll" in the real executable with exactly the given bytes on standard input, in a way that cannot reach
   /// the installed service whatever the executable makes of them. The server address is one no request can carry:
   /// written as JSON it alone is longer than the pipe accepts, and a request that large is refused before a
   /// connection is opened. So the executable ends in one of two ways, both before the service: it found no code and
   /// says a code is required, or it found one, built the request and was told the request is too large. That the
   /// address really is too large is checked here first, with the shortest code there could be, so a change to how
   /// requests are written fails this test before any process is started and not by contacting the service.
   /// </summary>
   private static Task<(int ExitCode, string Output, string Error)> EnrollWithBytesAsync(byte[] input)
   {
      Assert.True(JsonSerializer.Serialize(new PipeRequest("enroll", s_unsendableServer, "x"), PrintApiClient.JsonOptions).Length > AgentPipeFraming.MaximumCommandCharacters,
         "The server address no longer makes the request too large for the pipe, so this test could reach the installed service. It was not started.");
      return RunWithBytesAsync(input, "enroll", "--server", s_unsendableServer);
   }



   /// <summary>
   /// Starts the executable with the given arguments and standard input carrying the given line, if any. See
   /// <see cref="RunWithBytesAsync"/>, which does the work; the line goes in as UTF-8 with the platform's line end.
   /// </summary>
   private static Task<(int ExitCode, string Output, string Error)> RunAsync(string? input, params string[] args)
      => RunWithBytesAsync(input is null ? null : Encoding.UTF8.GetBytes(input + Environment.NewLine), args);



   /// <summary>
   /// Starts the executable with the given arguments and waits for it to end, returning its exit code and exactly what it
   /// wrote, line ends included. Standard input carries exactly the given bytes, if any, and is then closed. A program that ends
   /// without reading its input closes the pipe, and when it does so before the bytes are written the write fails; that is
   /// the program not reading, which is what such a test is showing, so it is not a failure of the test. The enrollment
   /// code variable is removed from the child's environment, so the result does not depend on the machine running the
   /// test. A process that has not finished within a minute is killed, so a hang fails the test instead of blocking the run.
   /// </summary>
   private static async Task<(int ExitCode, string Output, string Error)> RunWithBytesAsync(byte[]? input, params string[] args)
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
         try
         {
            if(input is not null)
            {
               await process.StandardInput.BaseStream.WriteAsync(input, limit.Token);
               await process.StandardInput.BaseStream.FlushAsync(limit.Token);
            }
            process.StandardInput.Close();
         }
         catch(IOException)
         {
            // The program closed its end of the pipe without reading: it had already decided what to do with these arguments.
         }
         await process.WaitForExitAsync(limit.Token);
         return (process.ExitCode, await output, await error);
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
