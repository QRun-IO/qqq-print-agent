/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What a server's administrator wants of this computer and the last thing they asked of it: whether the server has sent
/// its state at all, the desired state (dispatch paused or not, connected or disconnected) with its revision, the control
/// channel's last contact, and the last command with its outcome. It is how a client explains why nothing is being
/// claimed. It is a record of the contract, separate from the stored <see cref="AgentControlState"/> it is built from,
/// because that one is the form of a state file and changes with what the service needs to keep, while this one is what
/// clients of every later version read. The contact and command times are carried as the text the store holds, not
/// converted: they are ISO 8601 as the service wrote them, and a client parses them as such.
/// </summary>
public sealed record ServerControl(string Support, DeviceDesiredState DesiredState, string? LastContact = null, string? LastCommand = null, string? LastOutcome = null, string? LastCommandAt = null)
{
   /// <summary>Takes from the stored control state the members the contract carries, as they are stored.</summary>
   public static ServerControl From(AgentControlState state)
   {
      ArgumentNullException.ThrowIfNull(state);
      return new ServerControl(state.Support, state.DesiredState, state.LastContact, state.LastCommand, state.LastOutcome, state.LastCommandAt);
   }
}
