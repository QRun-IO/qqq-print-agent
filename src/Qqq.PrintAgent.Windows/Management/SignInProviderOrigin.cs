/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The one rule for comparing the sign-in provider a request says the operator was shown with the one the service read
/// from the server (ADR-0004, "Who names the sign-in provider"). <see cref="ManagementOperations.ServersAdd"/> and
/// <see cref="ManagementOperations.TrustSet"/> both store a certificate exception that covers the provider the server
/// names, and the service reads that name again when it stores, after the operator was shown what
/// <see cref="ManagementOperations.ServersCheck"/> reported. A server whose certificate is not verified could name
/// another provider the second time, or name one where it named none. The comparison exists so that what the operator
/// accepted is what the service uses: the request states what the check showed, the service compares, and when they
/// differ it stores nothing and answers <see cref="ManagementErrorCode.ProviderChanged"/>. The value is only ever
/// compared: what the service stores is its own read.
/// </summary>
internal static class SignInProviderOrigin
{
   /// <summary>
   /// True when what the operator was shown is what the service read. An origin that is absent states that the check
   /// named no provider, and it is compared like any other value: none shown and none read agree, and none shown and
   /// one read do not, because a provider that appears after the check is exactly the one the operator has not seen.
   /// Two origins agree when they are the same origin in the one form an origin is kept and compared in
   /// (<see cref="QqqOrigin.Of"/>: scheme, the host's ASCII name and port), so one place written two ways is one
   /// place, and another host, port or scheme is another. Text that names no origin agrees with nothing. An IPv6
   /// literal with a zone names none here, as it names no server's address (<see cref="ServerAddress.HasZone"/>):
   /// the text two origins are compared by has no place for the zone, so two zones, or a zone and none, would
   /// otherwise compare as one place.
   /// </summary>
   internal static bool Agrees(string? shown, string? readByTheService)
   {
      if(shown is null || readByTheService is null)
      {
         return shown is null && readByTheService is null;
      }
      return OriginOf(shown) is { } left && OriginOf(readByTheService) is { } right && string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);
   }



   /// <summary>The origin a text names, in the one form an origin is compared in, or null when it names none.</summary>
   private static Uri? OriginOf(string text)
      => Uri.TryCreate(text, UriKind.Absolute, out Uri? address) && QqqOrigin.Of(address) is { } origin && !ServerAddress.HasZone(origin) ? origin : null;
}
