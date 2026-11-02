/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>The server's answer to a control poll: the control version, the desired state and any pending commands. The version lets the agent skip a reply older than what it already applied.</summary>
public sealed record DeviceControlPoll(int ControlVersion, string ServerTime, int PollIntervalSeconds, DeviceDesiredState DesiredState, IReadOnlyList<DeviceControlCommand> Commands);
