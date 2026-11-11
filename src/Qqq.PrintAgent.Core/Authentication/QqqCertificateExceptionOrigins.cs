/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The development-only certificate exception of one server: the origins from which a certificate Windows does not
/// trust is accepted. There are at most two, the server's own origin and the origin of the sign-in provider that server
/// names, and a certificate from any other host is judged normally. The value is immutable and can only be built from
/// well-formed HTTPS origins, so an exception can never be widened after it was accepted and can never name
/// something that is not a place a certificate comes from. An empty value means no exception.
/// </summary>
public sealed class QqqCertificateExceptionOrigins
{
   /// <summary>The most origins an exception can cover: the server's own and its sign-in provider's.</summary>
   public const int MaximumOrigins = 2;



   /// <summary>
   /// Keeps the origins, which the factories have already normalized and counted, behind a read-only wrapper. The
   /// array is one a factory made for this value and hands to nothing else, and a caller that reads
   /// <see cref="Origins"/> gets a list it can neither write through nor cast back to the array, so the value is
   /// immutable in fact and not only by convention.
   /// </summary>
   private QqqCertificateExceptionOrigins(Uri[] origins)
   {
      Origins = Array.AsReadOnly(origins);
   }



   /// <summary>No exception: every certificate is judged as Windows judges it.</summary>
   public static QqqCertificateExceptionOrigins None { get; } = new([]);



   /// <summary>The origins the exception covers, each as an HTTPS address of scheme, the host's ASCII name and port with the path "/" (see <see cref="QqqOrigin.Of"/>); none for no exception. The list is read-only: an attempt to change it throws.</summary>
   public IReadOnlyList<Uri> Origins { get; }



   /// <summary>True when the exception covers nothing.</summary>
   public bool IsEmpty => Origins.Count == 0;



   /// <summary>
   /// Builds the exception for the given origins. Each must be an absolute HTTPS address with no user information,
   /// query or fragment and no path beyond "/", and its host must have an ASCII name; the same origin given twice
   /// counts once, and a name in Unicode and the same name in its "xn--" form are the same origin. More than two
   /// distinct origins, or one that is not such an address, is refused with an <see cref="ArgumentException"/>, so a
   /// wider or malformed exception cannot exist. No origins gives <see cref="None"/>.
   /// </summary>
   public static QqqCertificateExceptionOrigins For(params Uri[] origins)
   {
      ArgumentNullException.ThrowIfNull(origins);
      List<Uri> distinct = [];
      foreach(Uri origin in origins)
      {
         Uri normalized = Normalize(origin) ?? throw new ArgumentException("A certificate exception names HTTPS origins only: an absolute https address with no user information, path, query or fragment.", nameof(origins));
         if(!distinct.Contains(normalized))
         { distinct.Add(normalized); }
      }
      if(distinct.Count > MaximumOrigins)
      { throw new ArgumentException("A certificate exception covers at most two origins: the server's own and the sign-in provider it names.", nameof(origins)); }
      return distinct.Count == 0 ? None : new QqqCertificateExceptionOrigins([.. distinct]);
   }



   /// <summary>
   /// Builds the exception an operator accepted for the server at an address: the origin of that address and, when it
   /// is given, the origin of the sign-in provider that server names. Only the scheme, host and port of the server's
   /// address are used; whether the address is one a session may use at all is the session client's to decide. A
   /// server reached over plain HTTP presents no certificate, so its own origin is left out; the provider's is still
   /// covered. The provider's origin must be a well-formed HTTPS origin, as <see cref="For"/> requires.
   /// </summary>
   public static QqqCertificateExceptionOrigins ForServer(Uri server, Uri? provider = null)
   {
      ArgumentNullException.ThrowIfNull(server);
      Uri? own = server.IsAbsoluteUri && string.Equals(server.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ? QqqOrigin.Of(server) : null;
      return (own, provider) switch
      {
         (not null, null) => For(own),
         (not null, not null) => For(own, provider),
         (null, not null) => For(provider),
         _ => None,
      };
   }



   /// <summary>
   /// True when the address a request or a socket upgrade is going to is on one of the covered origins: the same host
   /// and port, over HTTPS or its WebSocket form. The host is compared by its ASCII name, as the session client's own
   /// same-origin rule compares it, so a name written in Unicode and the same name in its "xn--" form are one host.
   /// Anything else, a relative address or a host with no ASCII name included, is not covered.
   /// </summary>
   public bool Covers(Uri address)
   {
      ArgumentNullException.ThrowIfNull(address);
      return address.IsAbsoluteUri
         && (string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(address.Scheme, Uri.UriSchemeWss, StringComparison.Ordinal))
         && QqqOrigin.AsciiHost(address) is { } host
         && Origins.Any(origin => string.Equals(origin.IdnHost, host, StringComparison.OrdinalIgnoreCase) && origin.Port == address.Port);
   }



   /// <summary>The covered origins for a log line or a warning: "none", or the origins separated by commas. Each is shown with its host's ASCII name, the form it is kept in, so nothing a server wrote in a host name can look like another name or lay the line out differently.</summary>
   public override string ToString() => IsEmpty ? "none" : string.Join(", ", Origins.Select(origin => origin.GetLeftPart(UriPartial.Authority)));



   /// <summary>Returns the origin in its stored form (see <see cref="QqqOrigin.Of"/>), or null when the address is not a well-formed HTTPS origin or its host has no ASCII name.</summary>
   private static Uri? Normalize(Uri? origin)
   {
      return origin is not null && origin.IsAbsoluteUri && string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
         && origin.UserInfo.Length == 0 && origin.Query.Length == 0 && origin.Fragment.Length == 0 && string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal)
         ? QqqOrigin.Of(origin)
         : null;
   }
}
