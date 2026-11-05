/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// One destination in the server's operator printer catalog. The catalog lists only printers that are approved on the server,
/// present in their device's latest exposure snapshot and visible to the signed-in user, so an entry for this device's printer
/// is the server's confirmation that the printer is approved. Extra members the server adds are ignored.
/// </summary>
public sealed record OperatorPrinter(string PrinterRowId, string DeviceId, string PrinterId, string Name, string DeviceName, string Status, string[] Formats);
