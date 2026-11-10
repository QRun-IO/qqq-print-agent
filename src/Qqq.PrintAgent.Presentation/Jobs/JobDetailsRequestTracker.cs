/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>
/// Keeps a slow job-details reply from being shown in the wrong place. A row's details are loaded on demand, and by the
/// time the service answers the row may have been collapsed, reused for another job, or reloaded; each of those advances a
/// generation number, and a reply is applied only if it still carries the latest one. Without this, details for one job
/// could appear under another, or an old reply could end the loading state of a newer request.
/// </summary>
public sealed class JobDetailsRequestTracker
{
   private long _generation;



   /// <summary>True while a request is on its way and has not been invalidated, which is when a second request must not start and the refresh button is disabled.</summary>
   public bool IsLoading { get; private set; }



   /// <summary>Starts a request unless one is already loading, handing back its generation number so its reply can later be checked; at most one request is in flight per row.</summary>
   public bool TryBegin(out long generation)
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
   public bool IsCurrent(long generation) => IsLoading && generation == _generation;



   /// <summary>Marks the request finished, but only if it is still the current one, so a late finish of a stale request cannot cancel a newer load.</summary>
   public void Complete(long generation)
   {
      if(IsCurrent(generation))
      {
         IsLoading = false;
      }
   }



   /// <summary>Advances the generation and clears the loading flag, which makes every reply still on its way stale.</summary>
   public void Invalidate()
   {
      _generation++;
      IsLoading = false;
   }
}
