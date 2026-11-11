/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.WorkerFixture;

/// <summary>
/// A harmless child program for tests of code that starts a program and waits for it. It fills its error stream, prints
/// what it is told and exits with the code it is told, or it waits until it is killed. It changes nothing on the
/// computer except one file the test names, in which it leaves its process id.
/// </summary>
internal static class StandIn
{
   /// <summary>The first argument that selects this behavior of the fixture.</summary>
   internal const string Flag = "--stand-in";



   /// <summary>
   /// Runs one of two behaviors. <c>--stand-in exit CODE TEXT COUNT</c> writes COUNT characters to the error stream,
   /// then TEXT to the output stream, and exits with CODE; the error stream comes first, so a parent that does not read
   /// it never sees TEXT. <c>--stand-in wait FILE</c> writes its process id to FILE and then never exits by itself.
   /// </summary>
   internal static int Run(string[] args)
   {
      if(string.Equals(args[1], "wait", StringComparison.Ordinal))
      {
         File.WriteAllText(args[2], Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
         Thread.Sleep(Timeout.Infinite);
      }
      Console.Error.Write(new string('e', int.Parse(args[4], CultureInfo.InvariantCulture)));
      Console.Error.Flush();
      Console.Out.Write(args[3]);
      return int.Parse(args[2], CultureInfo.InvariantCulture);
   }
}
