/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// What a log fake has recorded, oldest first. It reads like the list it stands for, and every read and write takes
/// one lock, because the code under test can write on another thread while the test reads: a request whose caller
/// stopped waiting is followed to its end on whatever thread the pipe answers it on. A test that expects something
/// written that way waits for it (<see cref="Added"/>) and never assumes that it was written inside the call that
/// caused it.
/// </summary>
internal sealed class RecordedList<T> : IReadOnlyList<T>
{
   /// <summary>Guards everything below.</summary>
   private readonly Lock _gate = new();



   /// <summary>What was recorded, oldest first.</summary>
   private readonly List<T> _items = [];



   /// <summary>Those waiting for an item to have been recorded.</summary>
   private readonly List<(Predicate<T> Wanted, TaskCompletionSource Added)> _watchers = [];



   /// <summary>How many items have been recorded.</summary>
   public int Count
   {
      get
      {
         lock(_gate)
         {
            return _items.Count;
         }
      }
   }



   /// <summary>An item by its position, oldest first.</summary>
   public T this[int index]
   {
      get
      {
         lock(_gate)
         {
            return _items[index];
         }
      }
   }



   /// <summary>Walks the items as they are at the moment of the call; one recorded meanwhile is not among them.</summary>
   public IEnumerator<T> GetEnumerator()
   {
      List<T> now;
      lock(_gate)
      {
         now = [.. _items];
      }
      return now.GetEnumerator();
   }



   /// <summary>Walks the items as they are at the moment of the call.</summary>
   IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();



   /// <summary>Records one item.</summary>
   internal void Add(T item) => AddRange([item]);



   /// <summary>Records several items in order, and tells those who waited for one of them.</summary>
   internal void AddRange(IEnumerable<T> items)
   {
      TaskCompletionSource[] added;
      lock(_gate)
      {
         _items.AddRange(items);
         added = [.. _watchers.Where(watcher => _items.Exists(watcher.Wanted)).Select(watcher => watcher.Added)];
         _watchers.RemoveAll(watcher => _items.Exists(watcher.Wanted));
      }
      foreach(TaskCompletionSource watcher in added)
      {
         watcher.SetResult();
      }
   }



   /// <summary>Forgets everything recorded so far.</summary>
   internal void Clear()
   {
      lock(_gate)
      {
         _items.Clear();
      }
   }



   /// <summary>True when an item that is wanted has been recorded.</summary>
   internal bool Exists(Predicate<T> wanted)
   {
      lock(_gate)
      {
         return _items.Exists(wanted);
      }
   }



   /// <summary>
   /// Completes when an item that is wanted has been recorded, at once when one already has. It is how a test waits
   /// for something that is written on another thread. An item that never comes fails the test after a generous
   /// limit instead of hanging the run; the limit is a guard, never what a test asserts.
   /// </summary>
   internal Task Added(Predicate<T> wanted)
   {
      lock(_gate)
      {
         if(_items.Exists(wanted))
         {
            return Task.CompletedTask;
         }
         TaskCompletionSource added = new(TaskCreationOptions.RunContinuationsAsynchronously);
         _watchers.Add((wanted, added));
         return added.Task.WaitAsync(TimeSpan.FromSeconds(30));
      }
   }
}
