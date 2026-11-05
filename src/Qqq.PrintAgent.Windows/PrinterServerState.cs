/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// What the connected server has said about one locally shared printer. Sharing is a local choice; approval is the server's,
/// and the two are shown side by side so a printer that is shared but not yet approved is never mistaken for a working one.
/// </summary>
public static class PrinterServerState
{
   /// <summary>The server's catalog lists the printer for this device: it is approved and can receive jobs.</summary>
   public const string Approved = "APPROVED";

   /// <summary>The printer is shared and the server received the inventory, but its catalog does not list it: an administrator still has to approve it there.</summary>
   public const string AwaitingApproval = "AWAITING_APPROVAL";

   /// <summary>The printer is not shared, so the server is not offered it.</summary>
   public const string NotShared = "NOT_SHARED";

   /// <summary>The printer is shared but the server's catalog could not be read (older server, or the signed-in user may not read it), so approval is unknown.</summary>
   public const string NotReported = "NOT_REPORTED";

   /// <summary>The printer is shared and no server has been asked yet (not connected, or the first check is still pending).</summary>
   public const string NotChecked = "NOT_CHECKED";
}

