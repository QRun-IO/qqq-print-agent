/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Win32.SafeHandles;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The Windows facts the pipe client's trust decision rests on: which process serves a pipe, what the service control
/// manager says about a service, and a process to be looked at. An implementation only asks Windows and passes on what
/// it said, failures included; whether those facts add up to "the installed print service" is decided by
/// <see cref="AgentPipePeer"/>.
/// </summary>
internal interface IPipePeerFacts
{
   /// <summary>The id of the process that serves this pipe instance, as the kernel records it. It fails with what Windows reported when the pipe can no longer say.</summary>
   uint ServerProcessId(SafePipeHandle pipe);



   /// <summary>Takes hold of the service with the given name. Nothing is asked of Windows yet; a name no service has is refused by the first read.</summary>
   IPeerService OpenService(string serviceName);



   /// <summary>
   /// Takes hold of the running process with the given id. It throws <see cref="ArgumentException"/> when no running
   /// process has that id, and <see cref="OverflowException"/> for an id a process cannot have.
   /// </summary>
   IPeerProcess OpenProcess(uint processId);
}
