/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Transport;

/// <summary>
/// A cancellation token that fires when the caller cancels or when a time limit measured on the supplied clock runs
/// out, whichever is first. Every request and transfer limit Core enforces itself goes through it, so the limit is
/// counted on the injected <see cref="TimeProvider"/> and a test can reach it by moving a clock instead of waiting.
/// The exception is <c>HttpClient.Timeout</c>, which the API client also sets: the platform enforces that one on the
/// system clock and no clock can be injected into it. An infinite limit leaves only the caller's cancellation.
/// </summary>
internal sealed class Deadline : IDisposable
{
   private readonly CancellationTokenSource _limit;
   private readonly CancellationTokenSource _either;



   /// <summary>Starts the limit now on the given clock and ties it to the caller's token.</summary>
   internal Deadline(TimeProvider time, TimeSpan limit, CancellationToken token)
   {
      _limit = new CancellationTokenSource(limit, time);
      _either = CancellationTokenSource.CreateLinkedTokenSource(token, _limit.Token);
   }



   /// <summary>Cancelled by the caller or by the limit, whichever comes first.</summary>
   internal CancellationToken Token => _either.Token;



   /// <summary>Stops the clock and releases both sources; the token must not be used afterwards.</summary>
   public void Dispose()
   {
      _either.Dispose();
      _limit.Dispose();
   }
}
