/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What a server's administrator wants of this computer and the last thing they asked of it: whether the server has sent
/// its state at all, the desired state (dispatch paused or not, connected or disconnected) with its revision, the last
/// contact, and the last command with its outcome. It is how a client explains why nothing is being claimed.
/// </summary>
public sealed record ServerControl(string Support, DeviceDesiredState DesiredState, string? LastContact = null, string? LastCommand = null, string? LastOutcome = null, string? LastCommandAt = null)
{
   /// <summary>Takes the members the contract carries from the stored control state and leaves out the one obsolete member that record still has for version 1.</summary>
   public static ServerControl From(AgentControlState state)
   {
      ArgumentNullException.ThrowIfNull(state);
      return new ServerControl(state.Support, state.DesiredState, state.LastContact, state.LastCommand, state.LastOutcome, state.LastCommandAt);
   }
}
