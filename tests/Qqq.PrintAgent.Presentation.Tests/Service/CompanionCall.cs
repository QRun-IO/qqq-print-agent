/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>One operation the companion sends: its name on the wire, the name of the one client method that sends it, and that method called with a set of sample values.</summary>
internal sealed record CompanionCall(string Operation, string Method, Func<ManagementClient, SampleValues, CancellationToken, Task<CallResult>> Send)
{
   /// <summary>The operation's name, which is what a test's output shows for a case.</summary>
   public override string ToString() => Operation;
}
