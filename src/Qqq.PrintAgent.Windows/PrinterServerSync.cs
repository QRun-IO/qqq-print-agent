/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The last attempt to read the server's printer catalog: when it was made, whether the server reported approvals
/// (REPORTED), could not (NOT_REPORTED, with the reason) or has not been asked (NOT_CHECKED), and which server answered.
/// The time is carried as the text the service recorded, ISO 8601; it is not converted on the way to a client.
/// </summary>
public sealed record PrinterServerSync(string Outcome, string? CheckedAt = null, string? Detail = null, string? Server = null, string? Source = null)
{
   /// <summary>The reading came from the server's PRINTERS frame (printing extension 2.4, printer-approval-v1): authoritative and pushed on change.</summary>
   public const string RealtimeSource = "REALTIME";

   /// <summary>The reading came from the operator printer catalog (printing extension 2.3 fallback): read after inventory and about once a minute.</summary>
   public const string CatalogSource = "CATALOG";

   /// <summary>The catalog was read and the printer states are current as of <see cref="CheckedAt"/>.</summary>
   public const string Reported = "REPORTED";

   /// <summary>The catalog could not be read; <see cref="Detail"/> says why.</summary>
   public const string NotReported = "NOT_REPORTED";

   /// <summary>No server has been asked since the service started or the connection was reset.</summary>
   public const string NotChecked = "NOT_CHECKED";

   /// <summary>The state before any server contact.</summary>
   public static PrinterServerSync Initial { get; } = new(NotChecked);
}
