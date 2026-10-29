using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentStoreTests
{
   [Fact]
   public void Journal_RetainsSubmissionAndPendingEventsAcrossRestart()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-test-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = new(directory))
         {
            store.Record("j1", new PrintEvent("e1", "SUBMITTING"));
         }
         using(AgentStore restored = new(directory))
         {
            Assert.Equal("SUBMITTING", restored.GetState("j1"));
            Assert.Single(restored.Pending());
            restored.Acknowledge("e1");
            Assert.Empty(restored.Pending());
         }
      }
      finally
      {
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }

   [Fact]
   public void Configuration_IsProtectedAndRoundTripsUnderCurrentIdentity()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-test-" + Guid.NewGuid().ToString("N"));
      try
      {
         using AgentStore store = new(directory);
         AgentConfiguration configuration = new("https://example.com", "test-device", "not-a-real-secret");
         store.SaveConfiguration(configuration);
         Assert.Equal(configuration, store.LoadConfiguration());
         byte[] file = File.ReadAllBytes(Path.Combine(directory, "connection.dpapi"));
         Assert.DoesNotContain("not-a-real-secret", System.Text.Encoding.UTF8.GetString(file), StringComparison.Ordinal);
      }
      finally
      {
         Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
         Directory.Delete(directory, recursive: true);
      }
   }
}
