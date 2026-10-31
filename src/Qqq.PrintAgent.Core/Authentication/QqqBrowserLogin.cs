/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>An in-memory, single-use browser attempt. The host owns callback delivery, never provider tokens.</summary>
public sealed class QqqBrowserLogin
{
   internal QqqBrowserLogin(Uri authorizationUri, Uri redirectUri, string state, string verifier, string issuer, long startedAt)
   {
      AuthorizationUri = authorizationUri;
      RedirectUri = redirectUri;
      State = state;
      Verifier = verifier;
      Issuer = issuer;
      StartedAt = startedAt;
   }

   public Uri AuthorizationUri { get; }
   public Uri RedirectUri { get; }
   internal string State { get; }
   internal string Verifier { get; }
   internal string Issuer { get; }
   internal long StartedAt { get; }
   public override string ToString() => "QQQ browser sign-in attempt (redacted)";
}
