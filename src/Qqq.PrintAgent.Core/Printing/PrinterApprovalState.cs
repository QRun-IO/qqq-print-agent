/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// One entry of the server's PRINTERS frame (printing extension 2.4, capability printer-approval-v1): a printer in this
/// device's latest accepted inventory and whether an administrator has approved it on the server.
/// </summary>
public sealed record PrinterApprovalState(string PrinterId, bool Enabled);
