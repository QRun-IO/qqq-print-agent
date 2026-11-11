/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Authentication;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// What a compatibility check found out about a server, for the connection wizard: every problem with its kind, the
/// discovery document when one was read, the sign-in methods on offer, the certificate that was refused when that is
/// why the connection failed, and the origin of the sign-in provider the server names. The provider's origin is there
/// whenever the sign-in description could be read and names one, whether or not sign-in is usable, so a caller can
/// show the operator both addresses a certificate exception would cover before the exception is saved. It is a
/// finding, not a failure: an unusable server produces a result, never an exception, so the wizard can show the
/// operator what to fix. No field carries a credential.
/// </summary>
public sealed record QqqServerCompatibility(IReadOnlyList<QqqCompatibilityProblem> Problems, QqqApiDiscovery? Discovery, QqqSignInMethods SignInMethods,
   QqqCertificateRejection? Certificate = null, Uri? ProviderOrigin = null)
{
   /// <summary>True when nothing stands between the operator and signing in: the server answered, is a QQQ server with a usable printing extension, and offers a sign-in method.</summary>
   public bool IsCompatible => Problems.Count == 0;



   /// <summary>The earliest check that failed, or null when all passed; every stage before it can be shown as passed.</summary>
   public QqqCompatibilityStage? FailedStage => Problems.Count == 0 ? null : Problems.Min(problem => problem.Stage);



   /// <summary>True when the result contains a problem of the given kind.</summary>
   public bool Has(QqqCompatibilityIssue issue) => Problems.Any(problem => problem.Issue == issue);



   /// <summary>
   /// Turns a failed request into the problem it stands for, or null for a failure that says nothing about the server (a
   /// programming error, for instance) and must keep propagating. A timeout, a name that does not resolve and a refused
   /// connection are "unreachable"; a failed TLS handshake is a secure connection failure, explained by the refused
   /// certificate when there was one; an answer that is not the expected document is reported as the given issue.
   /// The caller decides beforehand whether a cancellation was its own.
   /// </summary>
   internal static QqqCompatibilityProblem? FromFailure(Exception exception, QqqCompatibilityIssue unreadable, string document, QqqCertificateRejection? certificate)
   {
      return exception switch
      {
         QqqSessionException session => new(unreadable, session.Failure switch
         {
            QqqSessionException.FailureKind.TransportRefused => $"the server refused or redirected the {document} request",
            QqqSessionException.FailureKind.AuthenticationRequired or QqqSessionException.FailureKind.PermissionDenied => $"the server asks for authorization before answering the {document} request",
            _ => $"the {document} answer was not a valid QQQ document (a sign-in page or proxy error page in place of the API is a common cause)",
         }),
         OperationCanceledException => new(QqqCompatibilityIssue.ServerUnreachable, "the server did not answer in time"),
         HttpRequestException or IOException => IsSecureConnectionFailure(exception)
            ? new(QqqCompatibilityIssue.SecureConnectionFailed, SecureConnectionDetail(certificate))
            : new(QqqCompatibilityIssue.ServerUnreachable, UnreachableDetail(exception)),
         _ => null,
      };
   }



   /// <summary>True when the failure, or any cause behind it, is a TLS failure: the platform reports those either as a secure connection error or as an authentication exception underneath a generic request failure.</summary>
   private static bool IsSecureConnectionFailure(Exception exception)
   {
      for(Exception? current = exception; current is not null; current = current.InnerException)
      {
         if(current is AuthenticationException or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
         { return true; }
      }
      return false;
   }



   /// <summary>Explains a secure connection failure from the certificate that was refused, or as a plain handshake failure when no certificate was the cause.</summary>
   private static string SecureConnectionDetail(QqqCertificateRejection? certificate)
   {
      if(certificate is null)
      { return "the secure connection could not be established (the TLS handshake failed)"; }
      if(certificate.IsUntrustedAuthority)
      { return "Windows does not trust the authority that issued the server's certificate"; }
      return certificate.IsNameMismatch
         ? "the server's certificate was issued for a different name or address"
         : "the server presented no usable certificate";
   }



   /// <summary>Explains why the server could not be reached, from the platform's own classification of the failed request.</summary>
   private static string UnreachableDetail(Exception exception) => exception switch
   {
      HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "the server name could not be found",
      HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "the server refused the connection or is not listening on that port",
      _ => "the connection failed before the server answered",
   };
}
