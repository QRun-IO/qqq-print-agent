/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public sealed record DeviceControlPoll(int ControlVersion, string ServerTime, int PollIntervalSeconds, DeviceDesiredState DesiredState, IReadOnlyList<DeviceControlCommand> Commands);
