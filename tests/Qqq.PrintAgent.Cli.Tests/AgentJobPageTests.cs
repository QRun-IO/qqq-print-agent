/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Microsoft.Data.Sqlite;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentJobPageTests
{
   [Fact]
   public void SortedQueue_PreservesPaginationFilteringAndLocalDeduplication()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-page-queue-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = new(directory);
         store.Record("local", new PrintEvent("local-event", "SPOOLED"));
         store.Record("duplicate", new PrintEvent("duplicate-event", "SPOOLED"));
         JobSnapshot[] queued = [new("cloud", "QUEUED", "now", null, null, "laser", "Warehouse", "Packing slip", CreatedAt: "2099-01-01T00:00:00.0000000+00:00"), new("duplicate", "QUEUED", "now", null, null)];
         JobPage first = AgentRuntime.QueryJobsWithQueue(store, new JobQuery(PageSize: 1), queued);
         Assert.Equal("cloud", Assert.Single(first.Jobs).JobId);
         Assert.Equal(3, first.TotalCount);
         Assert.False(first.ServerQueueLimited);
         JobPage next = AgentRuntime.QueryJobsWithQueue(store, new JobQuery(Offset: 1, PageSize: 2), queued);
         Assert.Equal(2, next.Jobs.Count);
         Assert.DoesNotContain(next.Jobs, job => string.Equals(job.JobId, "cloud", StringComparison.Ordinal));
         Assert.Equal("cloud", Assert.Single(AgentRuntime.QueryJobsWithQueue(store, new JobQuery(Search: "packing", Status: "QUEUED", PrinterId: "laser"), queued).Jobs).JobId);
         Assert.Empty(AgentRuntime.QueryJobsWithQueue(store, new JobQuery(NeedsAttentionOnly: true), queued).Jobs);
         Assert.True(AgentRuntime.QueryJobsWithQueue(store, new JobQuery(), Enumerable.Repeat(queued[0], 50).ToArray()).ServerQueueLimited);
      }
      finally
      {
         SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }
}
