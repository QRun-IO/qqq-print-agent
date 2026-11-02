/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>The agent's report of what happened to a control command, keyed by a receipt id so a repeated report is recognised and the operator sees one outcome per command.</summary>
public sealed record DeviceControlReceipt(string ReceiptId, string Outcome, string CompletedAt, string? Message = null);
