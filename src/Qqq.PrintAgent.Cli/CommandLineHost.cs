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
/// <param name="Output">Standard output: service replies, and usage that was asked for.</param>
/// <param name="Error">Standard error: failures, usage shown because the arguments were wrong, and the enrollment prompt when the code is typed at a console. A command that succeeds without that prompt writes nothing here.</param>
/// <param name="ReadEnvironmentVariable">Reads one environment variable; the enrollment code arrives this way so it stays out of the command line and shell history.</param>
/// <param name="IsInputRedirected">Says whether standard input is a file or a pipe instead of a console, which decides how the enrollment code is read and whether it is prompted for.</param>
/// <param name="ReadLine">Reads one line of redirected standard input, for an enrollment code that is piped in; null at end of input.</param>
/// <param name="ReadKey">Reads one key from the console, for an enrollment code that is typed; the argument says whether to keep the key off the screen.</param>
/// <param name="SendAsync">Sends one request to the installed service over its administrator pipe and returns the reply text.</param>
/// <param name="ApplyUpdateAsync">Applies the prepared MSI update named by a request file and returns the exit code.</param>
/// <param name="ForwardSignInCallbackAsync">Forwards a browser sign-in callback address to the running companion.</param>
internal sealed record CommandLineHost(
   TextWriter Output,
   TextWriter Error,
   Func<string, string?> ReadEnvironmentVariable,
   Func<bool> IsInputRedirected,
   Func<string?> ReadLine,
   Func<bool, ConsoleKeyInfo> ReadKey,
   Func<PipeRequest, Task<string>> SendAsync,
   Func<string, Task<int>> ApplyUpdateAsync,
   Func<string, CancellationToken, Task> ForwardSignInCallbackAsync);
