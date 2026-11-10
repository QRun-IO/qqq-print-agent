/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.Security;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The certificate a server presented and Windows refused: who it was issued to and by, when it is valid, its
/// fingerprint, and why it was refused. An operator is shown this before choosing the certificate exception, so they see
/// what the exception would accept. All of it is public certificate data. When the server presented no certificate, only
/// the reasons are set.
/// </summary>
public sealed record PresentedCertificate(IReadOnlyList<string> Refusals, string? Subject = null, string? Issuer = null, DateTimeOffset? NotBefore = null, DateTimeOffset? NotAfter = null,
   string? Fingerprint = null)
{
   /// <summary>Refusal: Windows does not trust the authority that issued the certificate. This is the one case the certificate exception is for.</summary>
   public const string UntrustedAuthority = "untrustedAuthority";

   /// <summary>Refusal: the certificate was issued for a different name or address than the one entered.</summary>
   public const string NameMismatch = "nameMismatch";

   /// <summary>Refusal: the server presented no certificate.</summary>
   public const string NotPresented = "notPresented";



   /// <summary>
   /// Describes the certificate Core recorded as refused. The subject and issuer are taken as Core holds them, which is
   /// already printable and bounded, because they are text the server chose. The reasons are listed in a fixed order.
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
      return new PresentedCertificate(refusals, rejection.Subject, rejection.Issuer, AsUtc(rejection.NotBeforeUtc), AsUtc(rejection.NotAfterUtc), rejection.Thumbprint);
   }



   /// <summary>Reads a certificate date, which Core records in UTC, as an instant.</summary>
   private static DateTimeOffset? AsUtc(DateTime? value) => value is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;
}
