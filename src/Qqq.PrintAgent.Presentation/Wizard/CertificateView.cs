/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>
/// The certificate Windows refused, as the Server step shows it before the operator decides anything: which address
/// presented it, who it was issued to and by, when it is valid, its fingerprint, and why Windows refused it, in words.
/// All of it is public certificate data. A part the service did not report is null and is not shown; when the server
/// presented no certificate at all, only the address and the reasons are set.
/// </summary>
public sealed record CertificateView(string? PresentedBy, string? Subject, string? Issuer, string? ValidFrom, string? ValidTo, string? Fingerprint, IReadOnlyList<string> Refusals)
{
   /// <summary>
   /// Describes a refused certificate for the screen. The subject, the issuer and the fingerprint are text the server
   /// chose or the service passed on, so each is given the contract's printable form here; the record that carries
   /// them does not do that. The address is shown only in the form every origin is shown in (scheme, the host's
   /// ASCII name and port), and is left out when the contract takes the text for no origin. The dates are shown in
   /// the user's local time. A reason this version does not know is said to be that, once, and a certificate that
   /// came with no reason says that none was given, so the list is never empty.
   /// </summary>
   internal static CertificateView From(PresentedCertificate certificate, TimestampFormat timestamps)
   {
      List<string> refusals = [];
      foreach(string refusal in certificate.Refusals)
      {
         string sentence = WizardMessages.Refusal(refusal);
         if(!refusals.Contains(sentence, StringComparer.Ordinal))
         {
            refusals.Add(sentence);
         }
      }
      if(refusals.Count == 0)
      {
         refusals.Add(WizardMessages.RefusalNotGiven);
      }
      return new CertificateView(Origin(certificate.Origin), Shown(certificate.Subject), Shown(certificate.Issuer), Date(certificate.NotBefore, timestamps), Date(certificate.NotAfter, timestamps),
         Shown(certificate.Fingerprint), refusals);
   }



   /// <summary>
   /// An origin as it is shown: scheme, the host's ASCII name and the port when it is not the scheme's own, without a
   /// trailing slash. Null when the text is missing or names no origin, so nothing a server wrote is shown as an
   /// address unless it is one. What names an origin is the contract's judgement and no other
   /// (<see cref="SignInProviderOrigin.TryRead"/>), because the address shown for a sign-in provider is the one the
   /// operator accepts and a request then states: an address the contract refuses, such as an IPv6 literal with a
   /// zone, whose zone the shown form has no place for, is not shown with the zone dropped. It is not shown.
   /// </summary>
   internal static string? Origin(string? text) => SignInProviderOrigin.TryRead(text, out Uri? origin) ? origin.GetLeftPart(UriPartial.Authority) : null;



   /// <summary>Text the service passed on, in the contract's printable form; null when it is missing or shows nothing.</summary>
   private static string? Shown(string? text)
   {
      string shown = text is null ? string.Empty : ManagementText.Bounded(text, ManagementProtocol.MaximumMessageCharacters).Trim();
      return shown.Length == 0 ? null : shown;
   }



   /// <summary>A certificate date in the user's local time; null when the service reported none.</summary>
   private static string? Date(DateTimeOffset? date, TimestampFormat timestamps) => date is { } value ? timestamps.Full(value) : null;
}
