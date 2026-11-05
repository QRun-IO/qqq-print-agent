/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Transport trust chosen by an administrator for one server. <see cref="AllowUntrustedCertificate"/> makes the service and
/// companion accept a certificate Windows does not trust (self-signed or a private authority such as a local Caddy root),
/// but only for the server whose scheme, host and port equal <see cref="Origin"/>; a session for any other server keeps
/// normal validation, so a development exception cannot carry over to a production pairing. It is for development servers
/// only: with it on, a forged certificate for that server is also accepted. Every session created under it is logged with
/// a warning, and the setting stays until it is turned off or saved for a different server.
/// </summary>
public sealed record ServerTrust(bool AllowUntrustedCertificate = false, string? Origin = null)
{
   /// <summary>True when untrusted certificates are allowed and the given server is exactly the one the exception was saved for.</summary>
   public bool AppliesTo(Uri origin)
   {
      ArgumentNullException.ThrowIfNull(origin);
      return AllowUntrustedCertificate && Origin is not null && origin.IsAbsoluteUri
         && string.Equals(Normalize(origin), Origin, StringComparison.OrdinalIgnoreCase);
   }



   /// <summary>The scheme, host and port of a server address in the form the exception is saved as.</summary>
   public static string Normalize(Uri origin)
   {
      ArgumentNullException.ThrowIfNull(origin);
      return origin.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
   }
}
