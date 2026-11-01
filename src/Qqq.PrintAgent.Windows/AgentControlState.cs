/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>What the agent knows about operator control: server support, the desired state and the last contact and command outcome. It lets the UI explain why dispatch is paused or authorization is needed.</summary>
public sealed record AgentControlState(string Support, DeviceDesiredState DesiredState, bool AuthorizationRequired = false,
   string? LastContact = null, string? LastCommand = null, string? LastOutcome = null, string? LastCommandAt = null)
{
   /// <summary>The state before any server contact: support unknown and dispatch running, so the agent never assumes it has been paused or authorized.</summary>
   public static AgentControlState Initial => new("UNKNOWN", new DeviceDesiredState(0, false, "CONNECTED"));
}
