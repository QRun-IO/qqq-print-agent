/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent;

/// <summary>Fences asynchronous detail completions when a row or view is invalidated.</summary>
internal sealed class JobDetailsRequestTracker
{
   private long _generation;
   internal bool IsLoading { get; private set; }



   /// <summary>Starts a request unless one is already loading, handing back its generation number so its reply can later be checked; at most one request is in flight per row.</summary>
   internal bool TryBegin(out long generation)
   {
      generation = 0;
      if(IsLoading)
      {
         return false;
      }
      generation = ++_generation;
      IsLoading = true;
      return true;
   }



   /// <summary>True only while a request is loading and the generation is the latest, so a reply for a request that was since invalidated or replaced is recognised as stale.</summary>
   internal bool IsCurrent(long generation) => IsLoading && generation == _generation;



   /// <summary>Marks the request finished, but only if it is still the current one, so a late finish of a stale request cannot cancel a newer load.</summary>
   internal void Complete(long generation)
   {
      if(IsCurrent(generation))
      {
         IsLoading = false;
      }
   }



   /// <summary>Advances the generation and clears the loading flag, which makes every reply still on its way stale.</summary>
   internal void Invalidate()
   {
      _generation++;
      IsLoading = false;
   }
}
