/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The one form in which an origin is kept, compared and shown: the scheme, the host's name in plain ASCII, and the
/// port. A host name sent by a server can hold letters that look like other letters (a Cyrillic letter in place of
/// the "a" in "apple.com"), characters that show as nothing (a zero-width space) and characters that end a line
/// (U+2028). The ASCII name is the one the connection is really made to, "xn--..." for a name outside ASCII, so it
/// is what an operator has to see before a trust decision, and what two addresses must share to be the same place.
/// The session client's own same-origin rule compares that name as well, so a Unicode name and its "xn--" form are
/// one origin everywhere.
/// </summary>
public static class QqqOrigin
{
   /// <summary>
   /// Returns the origin of an absolute HTTP or WebSocket address (https, http, wss, ws) as scheme, ASCII host and
   /// port with the path "/", or null when the address is relative, has another scheme, or has a host with no plain
   /// ASCII name (one the platform could not convert, for instance because it holds a line separator). An IPv6
   /// literal keeps its brackets, an IPv4 address written short ("127.1") is already in its full form, and a default
   /// port is not shown by the result's <see cref="Uri.Authority"/>. Nothing but ASCII letters, digits and the
   /// punctuation of a host name can appear in the result, so it is safe in a log line and on a screen.
   /// </summary>
   public static Uri? Of(Uri? address)
   {
      if(address is null || !address.IsAbsoluteUri || address.Scheme is not ("https" or "http" or "wss" or "ws") || AsciiHost(address) is not { } host)
      { return null; }

      /////////////////////////////////////////////////////////////////////////
      // the text is rebuilt from a host a server may have written, so it is //
      // parsed again without throwing: a name the conversion left unusable  //
      // (an empty label, say) is no origin, and never an exception          //
      /////////////////////////////////////////////////////////////////////////
      string text = address.Scheme + Uri.SchemeDelimiter + (address.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host) + ":" + address.Port.ToString(CultureInfo.InvariantCulture) + "/";
      return Uri.TryCreate(text, UriKind.Absolute, out Uri? origin) ? origin : null;
   }



   /// <summary>
   /// The host and port of an address in the form they are shown in, in a log line or on a screen: the authority of
   /// the address's origin (see <see cref="Of"/>), so a name outside ASCII appears as the "xn--" name the connection
   /// is really made to and a default port is left out. An address that has no origin is shown as it was written,
   /// with every character that could end a line, reorder the text or show as nothing replaced by a space, and an
   /// address that names no host (a relative one, or the browser callback's custom scheme) is shown as nothing. This
   /// is the one place a host is formed for display, so a provider's host reads the same in every line that names
   /// it. The result never holds user information, a path, a query or a fragment.
   /// </summary>
   public static string ShownAuthority(Uri address)
   {
      ArgumentNullException.ThrowIfNull(address);
      if(Of(address) is { } origin)
      { return origin.Authority; }
      string written = address.IsAbsoluteUri ? address.Authority : "";
      return written.Length == 0 ? "" : QqqCompatibilityProblem.Printable(written, QqqCompatibilityProblem.TextLimit);
   }



   /// <summary>
   /// Returns the ASCII name of an absolute address's host, the name a connection to it is made to, or null when the
   /// host has none. The platform refuses to name some hosts (it throws for one that holds a line separator) and
   /// leaves others in Unicode (a name it does not treat as a domain name); neither is a name that may be compared
   /// or shown, so both are null here and no caller has to guard the conversion. The colon and percent sign of an
   /// IPv6 literal are accepted for a literal only.
   /// </summary>
   internal static string? AsciiHost(Uri address)
   {
      string host;
      try
      { host = address.IdnHost; }
      catch(UriFormatException)
      { return null; }
      bool literal = address.HostNameType == UriHostNameType.IPv6;
      return host.Length != 0 && host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' || (literal && character is ':' or '%')) ? host : null;
   }
}
