/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The typed answer to one request to the local service: a payload when it succeeded, the service's message when it refused,
/// the cause when no usable answer arrived, or a note that the caller stopped waiting. It replaces matching reply text by
/// prefix and catching pipe exceptions in every caller, so each caller decides what to show from <see cref="Outcome"/> and
/// <see cref="Unconfirmed"/> alone.
/// </summary>
public sealed class ServiceResult<T>
{
   /// <summary>Records one outcome; the factory methods below are the only callers, so the shapes stay consistent.</summary>
   private ServiceResult(ServiceOutcome outcome, T? payload, string message, string? reply, ServiceFailure failure, bool unconfirmed)
   {
      Outcome = outcome;
      Payload = payload;
      Message = message;
      Reply = reply;
      Failure = failure;
      Unconfirmed = unconfirmed;
   }



   /// <summary>Which way the request ended.</summary>
   public ServiceOutcome Outcome { get; }



   /// <summary>The data the service returned; set only when the request succeeded.</summary>
   public T? Payload { get; }



   /// <summary>The service's own text without its "Error:" marker when it refused, a plain reason when no usable answer arrived, and empty on success or when the caller stopped waiting.</summary>
   public string Message { get; }



   /// <summary>The reply exactly as the service sent it, for the few messages that show it whole; null when nothing arrived.</summary>
   public string? Reply { get; }



   /// <summary>What went wrong when no usable answer arrived; <see cref="ServiceFailure.None"/> otherwise.</summary>
   public ServiceFailure Failure { get; }



   /// <summary>
   /// True when the request left this process and no usable answer came back (a reply time limit, a pipe that closed
   /// mid-reply, an oversize or unreadable reply, or a wait the caller ended), so the service may have carried it out and
   /// the operator must check before repeating it. False when the service answered and when nothing was sent at all,
   /// which includes a wait that had already ended before the request was made.
   /// </summary>
   public bool Unconfirmed { get; }



   /// <summary>True when the request succeeded, which also tells the compiler the payload is present.</summary>
   [MemberNotNullWhen(true, nameof(Payload))]
   public bool Succeeded => Outcome == ServiceOutcome.Success;



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
