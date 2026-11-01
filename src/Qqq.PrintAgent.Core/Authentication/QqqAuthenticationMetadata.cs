/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>Provider metadata from QQQ. A public native bearer client is not a stored-session grant.</summary>
public sealed class QqqAuthenticationMetadata
{
   public const string NativeSessionFlow = "AUTHORIZATION_CODE_PKCE_SESSION";
   public string Name { get; init; } = "";
   public string Type { get; init; } = "";
   public ProviderValues? Values { get; init; }



   /// <summary>The provider settings the server publishes: client identifier, scopes, base address and, when the server supports it, the native browser sign-in grant.</summary>
   public sealed class ProviderValues
   {
      public string ClientId { get; init; } = "";
      public string BaseUrl { get; init; } = "";
      public string Scopes { get; init; } = "";
      public NativeGrant? NativeSessionLogin { get; init; }
   }



   /// <summary>The server's declaration that a native app may sign a user in through the browser with PKCE and receive a server-managed session: flow name, challenge method, prompt, issuer, discovery address and the approved redirect addresses.</summary>
   public sealed class NativeGrant
   {
      public string Flow { get; init; } = "";
      public string CodeChallengeMethod { get; init; } = "";
      public string Prompt { get; init; } = "";
      public string Issuer { get; init; } = "";
      public string DiscoveryUrl { get; init; } = "";
      public IReadOnlyList<string> RedirectUris { get; init; } = [];
   }
}
