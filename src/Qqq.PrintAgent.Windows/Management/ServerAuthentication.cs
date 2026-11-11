/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Who is signed in to a server, as the pipe carries it: whether a session exists, the principal and the tenant it is
/// for, the text that explains the state, and whether printing is authorized. It carries no cookie, password or device
/// proof. The principal and the tenant are values the server supplied, so they are never held as given: the record has
/// one constructor, which gives each the form of an identity value on the wire (<see cref="ManagementText.Identity"/>:
/// printable, and cut at <see cref="ManagementProtocol.MaximumIdentityCharacters"/> exactly), the same form
/// <see cref="IdentityChangedDetails"/> gives the approved account. So one account reads the same wherever a reply
/// names it, in a snapshot, in the result of adding a server or signing in, and in the details of a changed identity.
/// <see cref="From"/> builds the record from the service's own status through that constructor, and the serializer
/// reads the wire through it, so a client holds bounded values even when a service did not send them so. A value that
/// is missing is absent and left out of the text.
/// </summary>
public sealed record ServerAuthentication
{
   /// <summary>Takes the members as the service holds them or as the wire carried them, and gives the two identity values their wire form. <see cref="From"/> and the serializer are its only callers.</summary>
   [JsonConstructor]
   private ServerAuthentication(bool signedIn, string? principalId, string? tenantId, string message, bool authorized = false)
   {
      SignedIn = signedIn;
      PrincipalId = ManagementText.Identity(principalId);
      TenantId = ManagementText.Identity(tenantId);
      Message = message;
      Authorized = authorized;
   }



   /// <summary>True when this server has a session.</summary>
   public bool SignedIn { get; }



   /// <summary>The principal the session is for, printable and bounded, or null when there is none.</summary>
   public string? PrincipalId { get; }



   /// <summary>The tenant the session is for, printable and bounded, or null when there is none.</summary>
   public string? TenantId { get; }



   /// <summary>The text that explains the state to the operator. It is the service's own wording.</summary>
   public string Message { get; }



   /// <summary>True when the server has authorized this session to print.</summary>
   public bool Authorized { get; }



   /// <summary>Builds the record from the status the service keeps for a server, member by member.</summary>
   public static ServerAuthentication From(AgentAuthenticationStatus status)
   {
      ArgumentNullException.ThrowIfNull(status);
      return new ServerAuthentication(status.SignedIn, status.PrincipalId, status.TenantId, status.Message, status.Authorized);
   }
}
