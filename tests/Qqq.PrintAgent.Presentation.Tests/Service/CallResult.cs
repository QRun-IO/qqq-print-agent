/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>What one call of the client came back with, without its result type, so a test can walk every operation with one piece of code.</summary>
internal sealed record CallResult(ServiceOutcome Outcome, string? Code, ServiceFailure Failure, bool Unconfirmed, string Message, string? Reply, object? Payload)
{
   /// <summary>Copies a typed result.</summary>
   internal static CallResult Of<TResult>(ServiceResult<TResult> result) => new(result.Outcome, result.Code, result.Failure, result.Unconfirmed, result.Message, result.Reply, result.Payload);
}
