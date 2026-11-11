/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// The management pipe as a test scripts it: the "send one request, get the parsed reply" function the client is
/// built on, with no pipe behind it. A test says what each request in turn gets (an answer, a failure, or nothing
/// yet), reads back what was sent, and can wait for a request to arrive, so a test of concurrent requests decides the
/// order of events itself and never sleeps. A request the test did not script fails loudly instead of hanging.
/// </summary>
internal sealed class FakePipe
{
   /// <summary>Guards everything below; a request can arrive on another thread than the test's.</summary>
   private readonly Lock _gate = new();



   /// <summary>What the next requests get, in order.</summary>
   private readonly Queue<Func<Task<ManagementReplyParse>>> _script = new();



   /// <summary>Those waiting for a number of requests to have arrived.</summary>
   private readonly List<(int Count, TaskCompletionSource Arrived)> _watchers = [];



   /// <summary>True once every request the script has nothing for is held.</summary>
   private bool _holdsEverything;



   /// <summary>Every request that was sent, oldest first.</summary>
   internal List<ManagementRequest> Sent { get; } = [];



   /// <summary>The replies of the requests that are being held, in the order they were held. Completing one answers its request; failing one fails it.</summary>
   internal List<TaskCompletionSource<ManagementReplyParse>> Held { get; } = [];



   /// <summary>The function a client is built on.</summary>
   internal Task<ManagementReplyParse> SendAsync(ManagementRequest request)
   {
      Func<Task<ManagementReplyParse>> next;
      TaskCompletionSource[] arrived;
      lock(_gate)
      {
         Sent.Add(request);
         next = _script.Count > 0 ? _script.Dequeue() : HeldOrUnscripted(request);
         arrived = [.. _watchers.Where(watcher => watcher.Count <= Sent.Count).Select(watcher => watcher.Arrived)];
         _watchers.RemoveAll(watcher => watcher.Count <= Sent.Count);
      }
      foreach(TaskCompletionSource watcher in arrived)
      {
         watcher.SetResult();
      }
      return next();
   }



   /// <summary>The next request is answered with this reply.</summary>
   internal FakePipe Answer(ManagementReplyParse reply) => Then(() => Task.FromResult(reply));



   /// <summary>The next request fails with this exception after a task for it exists.</summary>
   internal FakePipe Fail(Exception failure) => Then(() => Task.FromException<ManagementReplyParse>(failure));



   /// <summary>The next request fails with this exception before a task for it exists.</summary>
   internal FakePipe Throw(Exception failure) => Then(() => throw failure);



   /// <summary>The next request gets whatever this function returns or throws.</summary>
   internal FakePipe Then(Func<Task<ManagementReplyParse>> next)
   {
      lock(_gate)
      {
         _script.Enqueue(next);
      }
      return this;
   }



   /// <summary>The next request is held: it stays unanswered until the test completes what this returns.</summary>
   internal TaskCompletionSource<ManagementReplyParse> Hold()
   {
      TaskCompletionSource<ManagementReplyParse> held = new();
      Then(() => held.Task);
      lock(_gate)
      {
         Held.Add(held);
      }
      return held;
   }



   /// <summary>From now on every request the script has nothing for is held, and <see cref="Held"/> lists them in the order they arrived.</summary>
   internal FakePipe HoldEverything()
   {
      lock(_gate)
      {
         _holdsEverything = true;
      }
      return this;
   }



   /// <summary>
   /// Completes when this many requests have been sent. It is how a test waits for a request that leaves on another
   /// thread, such as one that waited its turn. A request that never arrives fails the test after a generous limit
   /// instead of hanging the run; the limit is a guard, never what a test asserts.
   /// </summary>
   internal Task Arrived(int count)
   {
      lock(_gate)
      {
         if(Sent.Count >= count)
         {
            return Task.CompletedTask;
         }
         TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
         _watchers.Add((count, arrived));
         return arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
      }
   }



   /// <summary>A request as the wire carries it, by its position among those sent.</summary>
   internal string Wire(int index) => ManagementJson.Write(Sent[index]);



   /// <summary>What a request gets when the script has nothing for it: it is held when the test asked for that, and otherwise the test fails here. The caller holds the lock.</summary>
   private Func<Task<ManagementReplyParse>> HeldOrUnscripted(ManagementRequest request)
   {
      if(!_holdsEverything)
      {
         throw new InvalidOperationException($"The test scripted no reply for request {Sent.Count} ({request.Op}).");
      }
      TaskCompletionSource<ManagementReplyParse> held = new();
      Held.Add(held);
      return () => held.Task;
   }
}
