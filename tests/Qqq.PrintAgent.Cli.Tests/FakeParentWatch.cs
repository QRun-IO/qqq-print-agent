/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A parent watch that watches nothing: it records which process id it was asked to watch and whether it was running
/// when a test looked, and it stops when the helper cancels it, as the real one does. It never ends a process.
/// </summary>
internal sealed class FakeParentWatch : IParentWatch
{
   /// <summary>The process id the helper asked to have watched, or null when no watch was started.</summary>
   internal int? ParentId { get; private set; }



   /// <summary>True from the moment the watch was started until the helper cancelled it.</summary>
   internal bool Watching { get; private set; }



   /// <summary>True once the helper cancelled the watch and the watch's task completed.</summary>
   internal bool Stopped { get; private set; }



   /// <summary>Records the process id and returns a task that completes when the token is cancelled.</summary>
   public Task WatchAsync(int parentId, CancellationToken token)
   {
      ParentId = parentId;
      Watching = true;
      TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
      _ = token.Register(() =>
      {
         Watching = false;
         Stopped = true;
         done.SetResult();
      });
      return done.Task;
   }
}
