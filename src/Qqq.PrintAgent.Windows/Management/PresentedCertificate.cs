/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// A certificate that was presented to the service and that Windows refused: why it was refused, which address presented
/// it and when the service saw that, who it was issued to and by, when it is valid, and its fingerprint. An operator is
/// shown this before choosing the certificate exception, so they see what the exception would accept. All of it is
/// public certificate data. When the server presented no certificate, only the reasons, the address and the time are
/// set. A client treats a reason it does not know as unknown and still shows the certificate.
/// </summary>
public sealed record PresentedCertificate(IReadOnlyList<string> Refusals, string? Origin = null, DateTimeOffset? ObservedAt = null, string? Subject = null, string? Issuer = null,
   DateTimeOffset? NotBefore = null, DateTimeOffset? NotAfter = null, string? Fingerprint = null)
{
   /// <summary>Refusal: Windows does not trust the authority that issued the certificate. This is the one case the certificate exception is for.</summary>
   public const string UntrustedAuthority = "untrustedAuthority";



   /// <summary>Refusal: the certificate was issued for a different name or address than the one entered.</summary>
   public const string NameMismatch = "nameMismatch";



   /// <summary>Refusal: the server presented no certificate.</summary>
   public const string NotPresented = "notPresented";



   /// <summary>
   /// Describes the certificate Core recorded as refused, with the address that presented it and the time the service
   /// saw the refusal. Both come from the rejection itself and cannot be passed in: a session client talks to both the
   /// server and its sign-in provider, so the address is the one Core recorded as presenting this certificate, never
   /// one a caller assumes from which server it was working on. Core records it as an origin whose host is its ASCII
   /// name, and it is carried as scheme, host and port without a trailing slash; a host with no such name leaves it
   /// absent. The subject and issuer are taken as Core holds them, which is already printable and bounded, because
   /// they are text the server chose. The reasons are listed in a fixed order.
   /// </summary>
   public static PresentedCertificate From(QqqCertificateRejection rejection)
   {
      ArgumentNullException.ThrowIfNull(rejection);
      List<string> refusals = [];
      if(rejection.Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))
      {
         refusals.Add(UntrustedAuthority);
      }
      if(rejection.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
      {
         refusals.Add(NameMismatch);
      }
      if(rejection.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
      {
         refusals.Add(NotPresented);
      }
      return new PresentedCertificate(refusals, rejection.Origin?.GetLeftPart(UriPartial.Authority), rejection.ObservedAt, rejection.Subject, rejection.Issuer, AsUtc(rejection.NotBeforeUtc), AsUtc(rejection.NotAfterUtc), rejection.Thumbprint);
   }



   /// <summary>Reads a certificate date, which Core records in UTC, as an instant.</summary>
   private static DateTimeOffset? AsUtc(DateTime? value) => value is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;
}
