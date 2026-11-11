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
/// reads the wire through it, so a client holds bounded values even when a service did not send them so; a result
/// in which one of them is not text at all (the escape of a surrogate without its partner) is not read. A value that
/// is missing is absent and left out of the text. The message is the service's own wording today, and it is held to
/// the form of text on the wire all the same, by the same constructor: printable, and cut at
/// <see cref="ManagementProtocol.MaximumMessageCharacters"/> exactly, as an error's message is. So if a later handler
/// puts something a server said into it, that text cannot reach the wire or a client's screen raw.
/// </summary>
public sealed record ServerAuthentication
{
   /// <summary>Takes the members as the service holds them or as the wire carried them, and gives the two identity values and the message their wire form. <see cref="From"/> and the serializer are its only callers.</summary>
   [JsonConstructor]
   private ServerAuthentication(bool signedIn, string? principalId, string? tenantId, string message, bool authorized = false)
   {
      ArgumentNullException.ThrowIfNull(message);
      SignedIn = signedIn;
      PrincipalId = ManagementText.Identity(principalId);
      TenantId = ManagementText.Identity(tenantId);
      Message = ManagementText.Bounded(message, ManagementProtocol.MaximumMessageCharacters);
      Authorized = authorized;
   }



   /// <summary>True when this server has a session.</summary>
   public bool SignedIn { get; }



   /// <summary>The principal the session is for, printable and bounded, or null when there is none.</summary>
   public string? PrincipalId { get; }



   /// <summary>The tenant the session is for, printable and bounded, or null when there is none.</summary>
   public string? TenantId { get; }



   /// <summary>The text that explains the state to the operator: printable, and at most <see cref="ManagementProtocol.MaximumMessageCharacters"/> UTF-16 units. Today it is the service's own wording.</summary>
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
