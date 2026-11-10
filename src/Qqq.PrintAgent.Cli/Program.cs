/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli;

/// <summary>
/// Process entry point of qqq-print-agent.exe. It decides nothing: it hands the real console, environment, service pipe and
/// helper entry points to <see cref="CommandLine"/>, which holds every decision and is tested against fakes.
/// </summary>
public static class Program
{
   /// <summary>
   /// Binds the outside world and runs the arguments. Each boundary is passed as a method reference, not a wrapper, so there
   /// is no code here that a test cannot reach: asking for help exercises this whole composition without touching the service.
   /// </summary>
   public static Task<int> Main(string[] args)
      => new CommandLine(new CommandLineHost(Console.Out, Console.Error, Environment.GetEnvironmentVariable, IsInputRedirected, Console.ReadLine, Console.ReadKey,
         AgentPipeClient.SendAsync, MsiUpdateInstaller.ApplyAsync, QqqBrowserCallback.ForwardFromHandlerAsync)).RunAsync(args);



   /// <summary>Says whether standard input is a file or a pipe instead of a console. It exists only because a property cannot be passed as a method reference.</summary>
   internal static bool IsInputRedirected() => Console.IsInputRedirected;
}
