/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using System.IO.Pipes;

namespace Qqq.PrintAgent.WorkerFixture;

/// <summary>
/// A harmless child program for tests of code that starts a program and waits for it, or that asks Windows about
/// another process. It fills its error stream, prints what it is told and exits with the code it is told, or it waits
/// until it is killed, or it connects to a pipe the test serves and stays connected until it is killed. It changes
/// nothing on the computer except one file the test names, in which it leaves its process id.
/// </summary>
internal static class StandIn
{
   /// <summary>The first argument that selects this behavior of the fixture.</summary>
   internal const string Flag = "--stand-in";



   /// <summary>How long the connecting behavior waits for the pipe it was told to connect to, in milliseconds; after that it ends with a failure, so a pipe that never comes does not leave it running.</summary>
   private const int ConnectLimit = 60_000;



   /// <summary>
   /// Runs one of three behaviors. <c>--stand-in exit CODE TEXT COUNT</c> writes COUNT characters to the error stream,
   /// then TEXT to the output stream, and exits with CODE; the error stream comes first, so a parent that does not read
   /// it never sees TEXT. <c>--stand-in wait FILE</c> writes its process id to FILE and then never exits by itself.
   /// <c>--stand-in connect NAME</c> connects as the client of the named pipe NAME on this computer, sends nothing,
   /// and then never exits by itself.
   /// </summary>
   internal static int Run(string[] args)
   {
      if(string.Equals(args[1], "wait", StringComparison.Ordinal))
      {
         File.WriteAllText(args[2], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
         Thread.Sleep(Timeout.Infinite);
      }
      if(string.Equals(args[1], "connect", StringComparison.Ordinal))
      {
         using NamedPipeClientStream pipe = new(".", args[2], PipeDirection.InOut);
         pipe.Connect(ConnectLimit);
         Thread.Sleep(Timeout.Infinite);
      }
      Console.Error.Write(new string('e', int.Parse(args[4], CultureInfo.InvariantCulture)));
      Console.Error.Flush();
      Console.Out.Write(args[3]);
      return int.Parse(args[2], CultureInfo.InvariantCulture);
   }
}
