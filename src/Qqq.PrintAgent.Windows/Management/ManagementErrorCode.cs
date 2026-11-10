/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The codes a failed request can carry, spelled exactly as they appear on the wire and in the table of ADR-0004.
/// Clients switch on these and never on a message. A code is text, not an enumeration, so a client that meets a code
/// from a newer service can still show its message.
/// </summary>
public static class ManagementErrorCode
{
   /// <summary>The service does not speak the version the request names.</summary>
   public const string UnsupportedVersion = "unsupported_version";

   /// <summary>The operation does not exist.</summary>
   public const string UnknownOperation = "unknown_operation";

   /// <summary>The request's envelope or its arguments are missing or malformed, or it names a server where it must not, or omits one where it must.</summary>
   public const string InvalidRequest = "invalid_request";

   /// <summary>No server has this identifier.</summary>
   public const string ServerNotFound = "server_not_found";

   /// <summary>
   /// A name or an identifier prefix matches more than one server. The service never answers this: it takes a full
   /// identifier only. The command line produces it, from <see cref="ServerSelector"/>, after listing the servers, and
   /// names the identifiers that matched. It stays in this table so every client spells the refusal the same way.
   /// </summary>
   public const string ServerAmbiguous = "server_ambiguous";

   /// <summary>The computer already holds <see cref="ManagementProtocol.MaximumServers"/> servers.</summary>
   public const string ServerLimit = "server_limit";

   /// <summary>The address is already a server on this computer.</summary>
   public const string ServerExists = "server_exists";

   /// <summary>Another operation that changes this server's session, pairing or trust is running; this one was not started.</summary>
   public const string Busy = "busy";

   /// <summary>The operation needs a session this server does not have.</summary>
   public const string NotSignedIn = "not_signed_in";

   /// <summary>The operation needs a pairing this server does not have.</summary>
   public const string NotPaired = "not_paired";

   /// <summary>The compatibility check failed. The details are a <see cref="ServerCheckResult"/> with the stages.</summary>
   public const string ServerIncompatible = "server_incompatible";

   /// <summary>The compatibility check failed because Windows refused the certificate. The details are a <see cref="ServerCheckResult"/> with the stages and the certificate that was presented.</summary>
   public const string CertificateUntrusted = "certificate_untrusted";

   /// <summary>The server refused the session.</summary>
   public const string SignInRefused = "sign_in_refused";

   /// <summary>The server refused to bind the saved pairing to the session.</summary>
   public const string BindRefused = "bind_refused";

   /// <summary>The server bound the pairing for a different account than the one approved for it.</summary>
   public const string IdentityChanged = "identity_changed";

   /// <summary>The server refused the enrollment code.</summary>
   public const string EnrollmentRefused = "enrollment_refused";

   /// <summary>The outcome of an enrollment is not known. The details are an <see cref="EnrollmentUnconfirmedDetails"/> saying what was left behind.</summary>
   public const string EnrollmentUnconfirmed = "enrollment_unconfirmed";

   /// <summary>No such printer for this server.</summary>
   public const string PrinterNotFound = "printer_not_found";

   /// <summary>No such job for this server.</summary>
   public const string JobNotFound = "job_not_found";

   /// <summary>The job belongs to an earlier pairing and is history: it can be read, not acknowledged, retried or cancelled.</summary>
   public const string JobNotCurrent = "job_not_current";

   /// <summary>A retry could print twice and the request did not confirm that.</summary>
   public const string DuplicateRiskUnconfirmed = "duplicate_risk_unconfirmed";

   /// <summary>Stored state for this server cannot be read; the message says which.</summary>
   public const string StateUnreadable = "state_unreadable";

   /// <summary>The service is not running connections because a newer build wrote the stored state. The snapshot says so.</summary>
   public const string StateNewer = "state_newer";

   /// <summary>The service is not running connections because the upgrade from 1.x could not finish. The snapshot names the file it stopped at.</summary>
   public const string MigrationIncomplete = "migration_incomplete";

   /// <summary>The service is prepared for an update and is not accepting this operation.</summary>
   public const string UpdateInProgress = "update_in_progress";

   /// <summary>Anything else. The detail is in the service log, never in the reply.</summary>
   public const string InternalError = "internal_error";



   private static readonly Dictionary<string, Type> s_detailsTypes = new(StringComparer.Ordinal)
   {
      [ServerIncompatible] = typeof(ServerCheckResult),
      [CertificateUntrusted] = typeof(ServerCheckResult),
      [EnrollmentUnconfirmed] = typeof(EnrollmentUnconfirmedDetails),
   };



   /// <summary>Every code, in the order of the table in ADR-0004.</summary>
   public static IReadOnlyList<string> All { get; } =
   [
      UnsupportedVersion, UnknownOperation, InvalidRequest, ServerNotFound, ServerAmbiguous, ServerLimit, ServerExists, Busy, NotSignedIn, NotPaired,
      ServerIncompatible, CertificateUntrusted, SignInRefused, BindRefused, IdentityChanged, EnrollmentRefused, EnrollmentUnconfirmed,
      PrinterNotFound, JobNotFound, JobNotCurrent, DuplicateRiskUnconfirmed, StateUnreadable, StateNewer, MigrationIncomplete, UpdateInProgress, InternalError,
   ];



   /// <summary>The type of the details a code carries, or null for a code that carries none. Only the codes whose table entry promises details have a type.</summary>
   public static Type? DetailsType(string code) => s_detailsTypes.GetValueOrDefault(code);
}
