/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The typed answer to one request to the local service: a payload when it succeeded, the service's message when it refused,
/// the cause when no usable answer arrived, or a note that the caller stopped waiting. It replaces matching reply text by
/// prefix and catching pipe exceptions in every caller, so each caller decides what to show from <see cref="Outcome"/> and
/// <see cref="Unconfirmed"/> alone. A refusal from a service that speaks version 2 of the management pipe also carries
/// the contract's error code (<see cref="Code"/>) and, for the codes that have them, typed details
/// (<see cref="TryReadDetails"/>), so no caller decides anything by a message's text.
/// </summary>
public sealed class ServiceResult<T>
{
   /// <summary>The refusal as the contract carried it, kept for its details only. It is private, and its details have a fixed text form, so nothing shows them by reaching through a result.</summary>
   private readonly ManagementError? _error;



   /// <summary>Records one outcome; the factory methods below are the only callers, so the shapes stay consistent.</summary>
   private ServiceResult(ServiceOutcome outcome, T? payload, string message, string? reply, ServiceFailure failure, bool unconfirmed, string? code = null, ManagementError? error = null)
   {
      Outcome = outcome;
      Payload = payload;
      Message = message;
      Reply = reply;
      Failure = failure;
      Unconfirmed = unconfirmed;
      Code = code;
      _error = error;
   }



   /// <summary>Which way the request ended.</summary>
   public ServiceOutcome Outcome { get; }



   /// <summary>The data the service returned; set only when the request succeeded.</summary>
   public T? Payload { get; }



   /// <summary>The service's own text without its "Error:" marker when it refused, a plain reason when no usable answer arrived, and empty on success or when the caller stopped waiting.</summary>
   public string Message { get; }



   /// <summary>
   /// The reply exactly as a version 1 service sent it, for the few messages that show it whole; null when nothing arrived.
   /// It is always null for a result of <see cref="ManagementClient"/>: a version 2 reply is never kept as text, because
   /// one of them carries the identity provider's sign-out request.
   /// </summary>
   public string? Reply { get; }



   /// <summary>
   /// Why a version 2 service refused, as one of the <see cref="ManagementErrorCode"/> values; null unless
   /// <see cref="Outcome"/> is <see cref="ServiceOutcome.Rejected"/>, and null for a refusal from a version 1 service,
   /// which has no codes. It is always a code this version knows: a refusal that came with any other code carries
   /// <see cref="ManagementErrorCode.InternalError"/> here and the service's own message in <see cref="Message"/>, which
   /// is how the contract says an unknown code is handled. Callers decide by this and never by the message.
   /// </summary>
   public string? Code { get; }



   /// <summary>What went wrong when no usable answer arrived; <see cref="ServiceFailure.None"/> otherwise.</summary>
   public ServiceFailure Failure { get; }



   /// <summary>
   /// True when the request left this process and no usable answer came back (a reply time limit, a pipe that closed
   /// mid-reply, an oversize or unreadable reply, or a wait the caller ended), so the service may have carried it out and
   /// the operator must check before repeating it. False when the service answered and when nothing was sent at all,
   /// which includes a wait that had already ended before the request was made and a read that was skipped.
   /// <para>
   /// A reply that is not in this client's version of the pipe (<see cref="ServiceFailure.OlderService"/>,
   /// <see cref="ServiceFailure.NewerService"/>) is such an answer too, for every operation of
   /// <see cref="ManagementClient"/> that the contract's table does not call a read: the form of a reply says which
   /// side to update, and it is not proof that nothing was carried out. For a read it is false, because a read changes
   /// nothing whoever answered it.
   /// </para>
   /// <para>
   /// False does not always mean that nothing was done. When the service refused
   /// (<see cref="ServiceOutcome.Rejected"/>) it answered, so this is false, and <see cref="Code"/> says whether it
   /// acted: <see cref="ManagementErrorCode.EnrollmentUnconfirmed"/> means the outcome of an enrollment is not known,
   /// and <see cref="ManagementErrorCode.InternalError"/>, which is also what a code this version does not know
   /// becomes, says nothing either way.
   /// </para>
   /// </summary>
   public bool Unconfirmed { get; }



   /// <summary>True when the request succeeded, which also tells the compiler the payload is present.</summary>
   [MemberNotNullWhen(true, nameof(Payload))]
   public bool Succeeded => Outcome == ServiceOutcome.Success;



   /// <summary>
   /// Reads the details a refusal carries into the record the contract names for its code
   /// (<see cref="ManagementErrorCode.DetailsType"/>). It is false, with nothing read, when the request was not refused,
   /// when the code carries no details, when the type asked for is not the one the code's details have (so details
   /// can never be read as another code's record that happens to accept them), and when the details are missing or do
   /// not fit that record. A caller that gets false shows the message alone.
   /// </summary>
   public bool TryReadDetails<TDetails>([NotNullWhen(true)] out TDetails? details)
      where TDetails : class
   {
      details = null;
      return _error is not null && ManagementErrorCode.DetailsType(Code!) == typeof(TDetails) && ManagementJson.TryReadDetails(_error, out details);
   }



   /// <summary>A successful answer from a version 2 service carrying its result record. The reply's text is not kept.</summary>
   internal static ServiceResult<T> Success(T payload) => new(ServiceOutcome.Success, payload, string.Empty, null, ServiceFailure.None, unconfirmed: false);



   /// <summary>
   /// A refusal with one of the contract's codes and the message to show: the service's own refusal, with the error it
   /// sent so its details can be read, or (without one) a request the client did not send because the service would
   /// have refused it. A request that was not sent did nothing. For a refusal the service sent, the code decides
   /// whether it acted: most codes mean it did not, <see cref="ManagementErrorCode.EnrollmentUnconfirmed"/> means the
   /// outcome is not known, and <see cref="ManagementErrorCode.InternalError"/>, which an unknown code becomes, does
   /// not say. The result is not unconfirmed in any of these cases, because the service answered; a caller that
   /// offers a repeat decides by the code.
   /// </summary>
   internal static ServiceResult<T> Refusal(string code, string message, ManagementError? error = null)
      => new(ServiceOutcome.Rejected, default, message, null, ServiceFailure.None, unconfirmed: false, code, error);



   /// <summary>The request was not sent because too many were already in flight; nothing happened.</summary>
   internal static ServiceResult<T> Skipped() => new(ServiceOutcome.Skipped, default, string.Empty, null, ServiceFailure.None, unconfirmed: false);



   /// <summary>A successful answer carrying its payload and the reply it was read from.</summary>
   internal static ServiceResult<T> Success(T payload, string reply) => new(ServiceOutcome.Success, payload, string.Empty, reply, ServiceFailure.None, unconfirmed: false);



   /// <summary>An answer in which the service reported an error or refused, with the message to show and the whole reply.</summary>
   internal static ServiceResult<T> Rejection(string message, string reply) => new(ServiceOutcome.Rejected, default, message, reply, ServiceFailure.None, unconfirmed: false);



   /// <summary>No usable answer: the cause, whether the request had been sent, and a plain reason a message can quote.</summary>
   internal static ServiceResult<T> Unreachable(ServiceFailure failure, bool sent, string reason) => new(ServiceOutcome.Unavailable, default, reason, null, failure, unconfirmed: sent);



   /// <summary>The caller ended the wait; the request may still be carried out.</summary>
   internal static ServiceResult<T> Abandoned() => new(ServiceOutcome.Canceled, default, string.Empty, null, ServiceFailure.None, unconfirmed: true);



   /// <summary>The caller had ended the wait before the request was made, so nothing was sent and there is nothing to check.</summary>
   internal static ServiceResult<T> NotStarted() => new(ServiceOutcome.Canceled, default, string.Empty, null, ServiceFailure.None, unconfirmed: false);
}
