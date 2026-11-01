/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>An opaque QQQ session for protected storage by its host. It contains no provider refresh token.</summary>
public sealed class QqqSessionCredential
{
   /// <summary>Binds a session token to the one server origin it belongs to. The token must be visible ASCII of at most 4096 characters without separators, quotes or backslashes, so it can be placed in a cookie header without changing its meaning.</summary>
   public QqqSessionCredential(Uri origin, string sessionToken)
   {
      ArgumentNullException.ThrowIfNull(origin);
      ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
      if(sessionToken.Length > 4096 || sessionToken.Any(character => character < 33 || character > 126 || ";,\"\\\\".Contains(character, StringComparison.Ordinal)))
      {
         throw new ArgumentException("Invalid QQQ session credential.", nameof(sessionToken));
      }
      Origin = origin;
      SessionToken = sessionToken;
   }



   public Uri Origin { get; }
   public string SessionToken { get; }



   /// <summary>Returns a fixed redacted label, so logging or displaying the credential can never reveal the session token.</summary>
   public override string ToString() => "QQQ session credential (redacted)";
}
