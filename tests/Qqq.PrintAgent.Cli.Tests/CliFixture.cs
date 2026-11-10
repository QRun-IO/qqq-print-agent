/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Shared harness for the command-line tests: the real <see cref="CommandLine"/> wired to hand-written fakes for the console,
/// the environment, the service pipe and the two helper entry points. A test scripts what the outside world answers, runs
/// an argument list, then reads exactly what was written, sent and started. Nothing here touches the real console, the
/// installed service or a printer. Captured output uses "\n" line endings so expected text reads the same on every machine.
/// </summary>
internal sealed class CliFixture : IDisposable
{
   private readonly StringWriter _output = new() { NewLine = "\n" };
   private readonly StringWriter _error = new() { NewLine = "\n" };



   /// <summary>Everything written to standard output so far.</summary>
   internal string Output => _output.ToString();



   /// <summary>Everything written to standard error so far.</summary>
   internal string Error => _error.ToString();



   /// <summary>The environment the command line sees; a name that is absent reads as not set.</summary>
   internal Dictionary<string, string?> Variables { get; } = new(StringComparer.Ordinal);



   /// <summary>The names of the environment variables that were read, in order.</summary>
   internal List<string> VariablesRead { get; } = [];



   /// <summary>The lines an operator would type, in order; when they run out, standard input is at its end.</summary>
   internal Queue<string?> TypedLines { get; } = new();



   /// <summary>How many times a line was asked for from standard input.</summary>
   internal int LinesRead { get; private set; }



   /// <summary>The reply the fake service gives to every request.</summary>
   internal string Reply { get; set; } = "Synthetic service reply.";



   /// <summary>When set, the fake pipe fails with this instead of replying, after recording the request.</summary>
   internal Exception? SendFailure { get; set; }



   /// <summary>Every request sent to the fake service, in order.</summary>
   internal List<PipeRequest> Sent { get; } = [];



   /// <summary>The exit code the fake update helper reports.</summary>
   internal int HelperExitCode { get; set; }



   /// <summary>When set, whichever helper entry point is started fails with this.</summary>
   internal Exception? HelperFailure { get; set; }



   /// <summary>The request-file paths handed to the update helper, in order.</summary>
   internal List<string> UpdateRequests { get; } = [];



   /// <summary>The sign-in callback addresses handed to the forwarder, each with the cancellation token it was given.</summary>
   internal List<(string Callback, CancellationToken Token)> Callbacks { get; } = [];



   /// <summary>Everything the command line wrote, to either stream; a secret must never appear in it.</summary>
   internal string Everything => Output + Error;



   /// <summary>Runs one invocation of the command line against the fakes and returns its exit code.</summary>
   internal Task<int> RunAsync(params string[] args)
      => new CommandLine(new CommandLineHost(_output, _error, ReadVariable, ReadLine, SendAsync, ApplyUpdateAsync, ForwardCallbackAsync)).RunAsync(args);



   /// <summary>Records which variable was asked for and answers from the scripted variables.</summary>
   private string? ReadVariable(string name)
   {
      VariablesRead.Add(name);
      return Variables.GetValueOrDefault(name);
   }



   /// <summary>Hands out the next typed line, or null once the operator has nothing more to type.</summary>
   private string? ReadLine()
   {
      LinesRead++;
      return TypedLines.Count > 0 ? TypedLines.Dequeue() : null;
   }



   /// <summary>Records the request, then replies or fails as scripted.</summary>
   private Task<string> SendAsync(PipeRequest request)
   {
      Sent.Add(request);
      return SendFailure is null ? Task.FromResult(Reply) : Task.FromException<string>(SendFailure);
   }



   /// <summary>Records the update request path and reports the scripted exit code or failure.</summary>
   private Task<int> ApplyUpdateAsync(string requestPath)
   {
      UpdateRequests.Add(requestPath);
      return HelperFailure is null ? Task.FromResult(HelperExitCode) : Task.FromException<int>(HelperFailure);
   }



   /// <summary>Records the callback address and token, then succeeds or fails as scripted.</summary>
   private Task ForwardCallbackAsync(string callback, CancellationToken token)
   {
      Callbacks.Add((callback, token));
      return HelperFailure is null ? Task.CompletedTask : Task.FromException(HelperFailure);
   }



   /// <summary>Releases the two capture writers.</summary>
   public void Dispose()
   {
      _output.Dispose();
      _error.Dispose();
   }
}
