/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The result of <see cref="ManagementOperations.ConnectionReconnect"/>: whether a reconnect was requested, or was not
/// because the server's administrator has disconnected this device and only they can reconnect it. A missing sign-in or
/// pairing is not an outcome but an error (<see cref="ManagementErrorCode.NotSignedIn"/>,
/// <see cref="ManagementErrorCode.NotPaired"/>), because a reconnect cannot repair either. A client treats an outcome it
/// does not know as not requested.
/// </summary>
public sealed record ReconnectResult(string Outcome)
{
   /// <summary>The live connection was closed and a new one is being made; the snapshot shows how it goes.</summary>
   public const string Requested = "requested";



   /// <summary>Nothing was done: the administrator disconnected this device on the server.</summary>
   public const string HeldByAdministrator = "heldByAdministrator";
}
