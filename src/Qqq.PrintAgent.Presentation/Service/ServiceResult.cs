/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The typed answer to one request to the local service: a payload when it succeeded, the service's message when it refused,
/// or the kind of failure when no usable answer arrived. It replaces matching reply text by prefix and catching pipe
/// exceptions in every caller, so each caller decides what to show from <see cref="Outcome"/> alone.
/// </summary>
public sealed class ServiceResult<T>
{
   /// <summary>Records one outcome; the factory methods below are the only callers, so the three shapes stay consistent.</summary>
   private ServiceResult(ServiceOutcome outcome, T? payload, string message, string? reply, ServiceFailure failure)
   {
      Outcome = outcome;
      Payload = payload;
      Message = message;
      Reply = reply;
      Failure = failure;
   }



   /// <summary>Which of the three ways the request ended.</summary>
   public ServiceOutcome Outcome { get; }



   /// <summary>The data the service returned; set only when the request succeeded.</summary>
   public T? Payload { get; }



   /// <summary>The service's own error text without its "Error:" marker when it refused, a plain reason when no usable answer arrived, and empty on success.</summary>
   public string Message { get; }



   /// <summary>The reply exactly as the service sent it, for the few messages that show it whole; null when nothing arrived.</summary>
   public string? Reply { get; }



   /// <summary>Why no usable answer arrived; <see cref="ServiceFailure.None"/> whenever the service answered.</summary>
   public ServiceFailure Failure { get; }



   /// <summary>True when the request succeeded, which also tells the compiler the payload is present.</summary>
   [MemberNotNullWhen(true, nameof(Payload))]
   public bool Succeeded => Outcome == ServiceOutcome.Success;



   /// <summary>True when the wait ended on a time limit or a cancellation, the two cases in which the service may have acted anyway.</summary>
   public bool Interrupted => Failure is ServiceFailure.TimedOut or ServiceFailure.Canceled;



   /// <summary>A successful answer carrying its payload and the reply it was read from.</summary>
   internal static ServiceResult<T> Success(T payload, string reply) => new(ServiceOutcome.Success, payload, string.Empty, reply, ServiceFailure.None);



   /// <summary>An answer in which the service reported an error or refused, with the message to show and the whole reply.</summary>
   internal static ServiceResult<T> Rejection(string message, string reply) => new(ServiceOutcome.Rejected, default, message, reply, ServiceFailure.None);



   /// <summary>No usable answer: the kind of failure and a plain reason a message can quote.</summary>
   internal static ServiceResult<T> Unreachable(ServiceFailure failure, string reason) => new(ServiceOutcome.Unavailable, default, reason, null, failure);
}
