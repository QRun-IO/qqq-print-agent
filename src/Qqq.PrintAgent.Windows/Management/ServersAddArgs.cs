/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.ServersAdd"/>: the address, the name, whether the operator accepted the
/// certificate exception, and the session from a sign-in the companion has completed. The sign-in provider's address is
/// not an argument: the service reads it from the server itself and never takes it from a client. The name must be one
/// a server may have (<see cref="ServerName"/>): the record cannot be built with another, in code or from a request, so
/// a request that gives one is refused as <see cref="ServerName.RefusalCode"/> when its arguments are read. The session
/// credential is a secret, so the text form of these arguments is fixed.
/// </summary>
public sealed record ServersAddArgs
{
   /// <summary>Takes the arguments. The name must pass <see cref="ServerName"/>'s rule and is held with surrounding white space removed.</summary>
   public ServersAddArgs(string address, string name, bool acceptCertificateException, QqqSessionCredential sessionCredential)
   {
      Address = address;
      Name = ServerName.Require(name);
      AcceptCertificateException = acceptCertificateException;
      SessionCredential = sessionCredential;
   }



   /// <summary>The server's address.</summary>
   public string Address { get; }



   /// <summary>The server's name, as it is stored.</summary>
   public string Name { get; }



   /// <summary>True when the operator accepted the development-only certificate exception for this server.</summary>
   public bool AcceptCertificateException { get; }



   /// <summary>The session from a sign-in the companion has completed. It is a secret.</summary>
   public QqqSessionCredential SessionCredential { get; }



   /// <summary>Returns a fixed label, so logging the arguments can never reveal the session credential.</summary>
   public override string ToString() => "Arguments of servers.add (session credential not shown)";
}
