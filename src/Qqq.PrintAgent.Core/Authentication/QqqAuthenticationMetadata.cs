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



   /// <summary>
   /// The origin of the sign-in provider this description names: scheme, host and port of the issuer in the native
   /// browser grant, with the host as its ASCII name (see <see cref="QqqOrigin.Of"/>). The server wrote the issuer,
   /// and an operator is shown this origin before deciding to trust it, so a look-alike letter appears as the
   /// "xn--" name it really is and no invisible or line-ending character can appear at all. Null when the server
   /// signs in with a password, publishes no such grant, or names an issuer that is not a plain HTTPS address or
   /// whose host has no ASCII name. It is derived from what the server sent and is never part of the document.
   /// </summary>
   [System.Text.Json.Serialization.JsonIgnore]
   public Uri? ProviderOrigin => Uri.TryCreate(Values?.NativeSessionLogin?.Issuer, UriKind.Absolute, out Uri? issuer)
      && string.Equals(issuer.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && issuer.UserInfo.Length == 0
      ? QqqOrigin.Of(issuer)
      : null;



   /// <summary>The provider settings the server publishes: client identifier, scopes, base address and, when the server supports it, the native browser sign-in grant.</summary>
   public sealed class ProviderValues
   {
      public string ClientId { get; init; } = "";
      public string BaseUrl { get; init; } = "";
      public string Scopes { get; init; } = "";
      public NativeGrant? NativeSessionLogin { get; init; }

      /// <summary>The server's opt-in to provider sign-out (OIDC RP-initiated logout) with the exact callbacks it will accept; absent on servers that do not offer it.</summary>
      public NativeLogout? NativeSessionLogout { get; init; }
   }



   /// <summary>The server's declaration that it can return the provider's end-session data at logout: flow name and the exact post-logout callbacks.</summary>
   public sealed class NativeLogout
   {
      public const string RpInitiatedFlow = "OIDC_RP_INITIATED_LOGOUT";
      public string Flow { get; init; } = "";
      public IReadOnlyList<string> PostLogoutRedirectUris { get; init; } = [];
   }



   /// <summary>The server's declaration that a native app may sign a user in through the browser with PKCE and receive a server-managed session: flow name, challenge method, prompt, issuer, discovery address and the approved redirect addresses.</summary>
   public sealed class NativeGrant
   {
      public string Flow { get; init; } = "";
      public string CodeChallengeMethod { get; init; } = "";
      public string Prompt { get; init; } = "";
      public string Issuer { get; init; } = "";
      public string DiscoveryUrl { get; init; } = "";

      /// <summary>The server's nonce policy for the code exchange. Absent on older servers; <see cref="NonceRequired"/> means the exchange must carry the attempt's own nonce as expectedNonce.</summary>
      public string? NonceValidation { get; init; }
      public const string NonceRequired = "REQUIRED";

      /// <summary>The server validates the nonce when one is sent; sending the attempt's own nonce is always correct under this policy.</summary>
      public const string NonceOptional = "OPTIONAL";
      public IReadOnlyList<string> RedirectUris { get; init; } = [];
   }
}
