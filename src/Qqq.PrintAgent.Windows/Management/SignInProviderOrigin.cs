/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The one rule for comparing the sign-in provider's origin a request carries with the one the service read from the
/// server (ADR-0004, "Who names the sign-in provider"). <see cref="ManagementOperations.ServersAdd"/> and
/// <see cref="ManagementOperations.TrustSet"/> both store a certificate exception that covers the provider the server
/// names, and the service reads that name again when it stores, after the operator was shown what
/// <see cref="ManagementOperations.ServersCheck"/> reported. A server whose certificate is not verified could name
/// another provider the second time. So the request carries the origin the operator was shown, the service compares,
/// and when they differ it stores nothing and answers <see cref="ManagementErrorCode.ProviderChanged"/>. The value is
/// only ever compared: what the service stores is its own read.
/// </summary>
internal static class SignInProviderOrigin
{
   /// <summary>
   /// True when the request does not contradict the service's read: it carries the same text, compared exactly because
   /// the request repeats what the service itself reported, or it carries no origin at all. An origin is compared
   /// whenever it is given; a request without one is not compared, which is how a request that switches an exception
   /// off, or a client that ran no check, is accepted. The record notes what that leaves uncaught: a server that named
   /// no provider when it was checked and names one now.
   /// </summary>
   internal static bool Agrees(string? carried, string? readByTheService) => carried is null || string.Equals(carried, readByTheService, StringComparison.Ordinal);
}
