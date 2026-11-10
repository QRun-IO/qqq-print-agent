/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli;

/// <summary>
/// Everything outside the process that the command line touches, each as a replaceable function, so <see cref="CommandLine"/>
/// runs in a test without the real console, the installed service or its pipe. <see cref="Program"/> supplies the real ones.
/// </summary>
/// <param name="Output">Standard output: service replies, usage that was asked for, and the enrollment prompt.</param>
/// <param name="Error">Standard error: failures and usage shown because the arguments were wrong.</param>
/// <param name="ReadEnvironmentVariable">Reads one environment variable; the enrollment code arrives this way so it stays out of the command line and shell history.</param>
/// <param name="ReadLine">Reads one line of standard input, for the enrollment code when the variable is not set; null at end of input.</param>
/// <param name="SendAsync">Sends one request to the installed service over its administrator pipe and returns the reply text.</param>
/// <param name="RunPrinterWorkerAsync">Runs this process as an isolated printer worker and returns its exit code.</param>
/// <param name="ApplyUpdateAsync">Applies the prepared MSI update named by a request file and returns the exit code.</param>
/// <param name="ForwardSignInCallbackAsync">Forwards a browser sign-in callback address to the running companion.</param>
internal sealed record CommandLineHost(
   TextWriter Output,
   TextWriter Error,
   Func<string, string?> ReadEnvironmentVariable,
   Func<string?> ReadLine,
   Func<PipeRequest, Task<string>> SendAsync,
   Func<Task<int>> RunPrinterWorkerAsync,
   Func<string, Task<int>> ApplyUpdateAsync,
   Func<string, CancellationToken, Task> ForwardSignInCallbackAsync);
