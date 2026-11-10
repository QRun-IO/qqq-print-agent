/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Everything about one server that a client shows without asking again: its local identity, name, address and place in the
/// operator's order; its health and the text that explains it; the count of unreviewed failures that drives the health; who
/// is signed in and whether printing is authorized; whether this computer is paired with it and as which device; the
/// transport and its traffic meter; the administrator's desired state and last command; the printers shared with it and
/// the server's word on each; the certificate exception; whether its stored state cannot be read; and whether it is being
/// removed. None of it is a credential: the device identifier names the pairing and proves nothing.
/// </summary>
public sealed record ServerSnapshot(string ServerId, string Name, string Address, int Position, string Health, string Status, int AttentionCount,
   AgentAuthenticationStatus Authentication, bool Paired, string? DeviceId, AgentTransportSnapshot Transport, ServerControl Control,
   IReadOnlyList<ServerPrinter> Printers, PrinterServerSync PrinterSync, CertificateExceptionState CertificateException, bool StateUnreadable, bool Removing)
{
   /// <summary>Health: connected and nothing needs the operator.</summary>
   public const string Green = "GREEN";

   /// <summary>Health: working toward a connection, or held by the administrator; nothing has failed.</summary>
   public const string Orange = "ORANGE";

   /// <summary>Health: something needs the operator, such as a sign-in or a failed job that has not been reviewed.</summary>
   public const string Red = "RED";
}
