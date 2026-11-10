/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Load balancer affinity. A QQQ server may run as several nodes behind a load balancer that names the node it chose in a
/// cookie and expects that cookie back, so one client's requests keep reaching one node. This client attaches its cookies by
/// hand and used to drop everything but the session, so its requests and its realtime socket could land on different nodes.
/// It now keeps the load balancer's own cookies, and nothing else: they are held in memory only, for the life of this client
/// object, sent only to the session origin, and replaced or removed whenever the load balancer rewrites them (which is how it
/// moves a client off a node that stopped). They carry routing, never identity, so they are not saved with the session and
/// signing out neither needs nor clears them.
/// </summary>
public sealed partial class QqqSessionClient
{
   private const int MaximumAffinityCookies = 8;
   private const int MaximumAffinityValueLength = 4096;
   private readonly Lock _affinityLock = new();
   private readonly SortedDictionary<string, string> _affinity = new(StringComparer.Ordinal);



   /// <summary>How many load balancer cookies are held; for the log and tests, never their values.</summary>
   internal int AffinityCookieCount
   {
      get
      {
         lock(_affinityLock)
         { return _affinity.Count; }
      }
   }



   /// <summary>
   /// Keeps or drops load balancer cookies named in a response's Set-Cookie headers. Only the Application Load Balancer
   /// family (names beginning AWSALB) is recognized. A cookie is ignored unless it is scoped to this host and the root path,
   /// has a plain bounded value and, when marked Secure, arrived over HTTPS; one the server expired is removed. A malformed
   /// header is skipped, because a routing hint must never fail a request.
   /// </summary>
   private void AdoptAffinity(IEnumerable<string> setCookieHeaders)
   {
      DateTimeOffset now = _timeProvider.GetUtcNow();
      bool secureOrigin = string.Equals(Origin.Scheme, "https", StringComparison.Ordinal);
      foreach(string header in setCookieHeaders)
      {
         string[] parts = header.Split(';');
         int separator = parts[0].IndexOf('=', StringComparison.Ordinal);
         if(header.Length > 8192 || separator <= 0)
         { continue; }
         string name = parts[0][..separator].Trim();
         if(!IsAffinityCookieName(name))
         { continue; }
         string value = parts[0][(separator + 1)..].Trim();
         bool expired = value.Length == 0;
         bool usable = value.Length <= MaximumAffinityValueLength && !value.Any(character => character is <= ' ' or '"' or ',' or '\\' or >= (char)0x7f);
         foreach(string attribute in parts.Skip(1))
         {
            string[] pair = attribute.Split('=', 2);
            string key = pair[0].Trim();
            string detail = pair.Length == 2 ? pair[1].Trim() : "";
            if(key.Equals("Max-Age", StringComparison.OrdinalIgnoreCase))
            { expired |= long.TryParse(detail, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds) && seconds <= 0; }
            else if(key.Equals("Expires", StringComparison.OrdinalIgnoreCase))
            { expired |= DateTimeOffset.TryParse(detail, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at) && at <= now; }
            else if(key.Equals("Domain", StringComparison.OrdinalIgnoreCase))
            { usable &= string.Equals(detail.TrimStart('.'), Origin.Host, StringComparison.OrdinalIgnoreCase); }
            else if(key.Equals("Path", StringComparison.OrdinalIgnoreCase))
            { usable &= string.Equals(detail, "/", StringComparison.Ordinal); }
            else if(key.Equals("Secure", StringComparison.OrdinalIgnoreCase))
            { usable &= secureOrigin; }
         }
         lock(_affinityLock)
         {
            if(expired)
            { _affinity.Remove(name); }
            else if(usable && (_affinity.ContainsKey(name) || _affinity.Count < MaximumAffinityCookies))
            { _affinity[name] = value; }
         }
      }
   }



   /// <summary>The Cookie header for a request to the session origin: the session cookies the caller passed, when any, followed by the load balancer cookies; null when there is neither.</summary>
   private string? WithAffinity(string? sessionCookie)
   {
      string affinity;
      lock(_affinityLock)
      {
         if(_affinity.Count == 0)
         { return sessionCookie; }
         affinity = string.Join("; ", _affinity.Select(pair => pair.Key + "=" + pair.Value));
      }
      return string.IsNullOrEmpty(sessionCookie) ? affinity : sessionCookie + "; " + affinity;
   }



   /// <summary>
   /// Reads the load balancer cookies from a WebSocket upgrade answer and releases the collected headers. It runs whether the
   /// upgrade was accepted or refused: a node that is stopping answers 502 or 503, and that answer carries the load
   /// balancer's new choice of node for the retry.
   /// </summary>
   private void AdoptSocketAffinity(System.Net.WebSockets.ClientWebSocket socket)
   {
      IReadOnlyDictionary<string, IEnumerable<string>>? headers = socket.HttpResponseHeaders;
      if(headers is null)
      { return; }
      foreach(KeyValuePair<string, IEnumerable<string>> header in headers)
      {
         if(header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
         { AdoptAffinity(header.Value); }
      }
      socket.HttpResponseHeaders = null;
   }



   /// <summary>True for the Application Load Balancer's cookie names: AWSALB, AWSALBCORS, AWSALBTG, AWSALBTGCORS and the AWSALBAPP series.</summary>
   private static bool IsAffinityCookieName(string name) => name.Length <= 64 && name.StartsWith("AWSALB", StringComparison.Ordinal)
      && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
