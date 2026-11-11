/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One installed queue as the Windows spooler lists it: its name, driver, port and status flags, copied out of the
/// spooler's answer so nothing that reads it holds native memory. A name Windows left out is null.
/// </summary>
internal sealed record SpoolerQueue(string? Name, string? Driver, string? Port, uint Status);
