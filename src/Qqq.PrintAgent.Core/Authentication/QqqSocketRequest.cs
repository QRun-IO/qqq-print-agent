/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Everything about one realtime socket upgrade, decided by the session client before any platform object is touched:
/// the address, the subprotocol to offer, the request headers (session cookie with the load balancer's cookies after it,
/// origin, client channel and device proof) and the keep-alive timing. The headers carry credentials, so the label is
/// fixed text.
/// </summary>
internal sealed record QqqSocketRequest(Uri Uri, string SubProtocol, IReadOnlyList<KeyValuePair<string, string>> Headers, TimeSpan KeepAliveInterval, TimeSpan KeepAliveTimeout)
{
   /// <summary>Returns a fixed redacted label, so logging or displaying a request can never reveal the cookie or device proof in its headers.</summary>
   public override string ToString() => "QQQ realtime socket request (redacted)";
}
