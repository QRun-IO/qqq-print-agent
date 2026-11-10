/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>Arguments of <see cref="ManagementOperations.ComputerSetProfile"/>: this computer's name, description and location. A member left out keeps what is stored; empty text clears it.</summary>
public sealed record ComputerSetProfileArgs(DeviceProfile Profile);
