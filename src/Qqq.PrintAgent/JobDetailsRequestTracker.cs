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



   internal bool IsCurrent(long generation) => IsLoading && generation == _generation;



   internal void Complete(long generation)
   {
      if(IsCurrent(generation))
      {
         IsLoading = false;
      }
   }



   internal void Invalidate()
   {
      _generation++;
      IsLoading = false;
   }
}
