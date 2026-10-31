/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>Credential-free local service observations for the tray companion.</summary>
public sealed record AgentSnapshot(bool Enrolled, bool Connected, string Health, string Status, string? ServerUrl, string? DeviceId, string? LastSuccessfulPoll, IReadOnlyList<PrinterSnapshot> Printers, IReadOnlyList<JobSnapshot> Jobs, IReadOnlyList<AgentLogEntry> Logs, DeviceProfile? DeviceProfile = null, AgentControlState? Control = null, AgentAuthenticationStatus? Authentication = null);

