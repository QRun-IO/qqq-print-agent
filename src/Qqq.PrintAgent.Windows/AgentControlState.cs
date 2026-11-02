/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed record AgentControlState(string Support, DeviceDesiredState DesiredState, bool AuthorizationRequired = false,
   string? LastContact = null, string? LastCommand = null, string? LastOutcome = null, string? LastCommandAt = null)
{
   public static AgentControlState Initial => new("UNKNOWN", new DeviceDesiredState(0, false, "CONNECTED"));
}
