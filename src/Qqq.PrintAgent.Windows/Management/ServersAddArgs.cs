/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.ServersAdd"/>: the address, the name, whether the operator accepted the
/// certificate exception, the session from a sign-in the companion has completed, and the sign-in provider's origin
/// that <see cref="ManagementOperations.ServersCheck"/> reported (<see cref="ServerCheckResult.ProviderOrigin"/>).
/// <para>
/// The provider's origin is carried for comparison only (ADR-0004, "Who names the sign-in provider"). The service still
/// reads the provider from the server itself and stores only what it read; it never takes the value from a client.
/// But it reads the sign-in description once for the check and again for the add, and a server whose certificate is
/// not verified could name another provider the second time, which a stored certificate exception would then cover
/// without the operator having seen it. So the request says which provider the operator was shown, the service
/// compares it with its own read (<see cref="NamesTheProvider"/>), and when they differ it stores nothing and answers
/// <see cref="ManagementErrorCode.ProviderChanged"/>. A request that leaves the origin out says that the check reported
/// no provider, and is refused the same way when the server names one.
/// </para>
/// <para>
/// The name must be one a server may have (<see cref="ServerName"/>): the record cannot be built with another, in code
/// or from a request, so a request that gives one is refused as <see cref="ServerName.RefusalCode"/> when its
/// arguments are read. The session credential is a secret, so the text form of these arguments is fixed.
/// </para>
/// </summary>
public sealed record ServersAddArgs
{
   /// <summary>Takes the arguments. The name must pass <see cref="ServerName"/>'s rule and is held with surrounding white space removed. The provider's origin is the one the check reported, or null when it reported none.</summary>
   public ServersAddArgs(string address, string name, bool acceptCertificateException, QqqSessionCredential sessionCredential, string? providerOrigin = null)
   {
      Address = address;
      Name = ServerName.Require(name);
      AcceptCertificateException = acceptCertificateException;
      SessionCredential = sessionCredential;
      ProviderOrigin = providerOrigin;
   }



   /// <summary>The server's address.</summary>
   public string Address { get; }



   /// <summary>The server's name, as it is stored.</summary>
   public string Name { get; }



   /// <summary>True when the operator accepted the development-only certificate exception for this server.</summary>
   public bool AcceptCertificateException { get; }



   /// <summary>The session from a sign-in the companion has completed. It is a secret.</summary>
   public QqqSessionCredential SessionCredential { get; }



   /// <summary>The sign-in provider's origin that the check reported and the operator was shown, or null when the check reported none. It is compared, never stored.</summary>
   public string? ProviderOrigin { get; }



   /// <summary>
   /// True when the sign-in provider the service read from the server is the one this request carries: the same text,
   /// compared exactly, because the request repeats what the service itself reported; or none on both sides. When it
   /// is false the service stores nothing and answers <see cref="ManagementErrorCode.ProviderChanged"/>.
   /// </summary>
   public bool NamesTheProvider(string? readByTheService) => string.Equals(ProviderOrigin, readByTheService, StringComparison.Ordinal);



   /// <summary>Returns a fixed label, so logging the arguments can never reveal the session credential.</summary>
   public override string ToString() => "Arguments of servers.add (session credential not shown)";
}
