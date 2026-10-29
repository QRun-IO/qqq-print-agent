/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentActivityLogTests
{
   /// <summary>Credential-shaped message and exception fields never reach the shared AppKit file.</summary>
   [Fact]
   public void RedactsCredentialsBeforePersistingMessagesAndExceptions()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-log-tests", Guid.NewGuid().ToString("N"));
      try
      {
         IActivityLog log = AgentActivityLog.Create(directory);
         log.Info("[Companion] Authorization: Bearer synthetic-bearer deviceToken=synthetic-device enrollmentCode=\"synthetic-code\" https://operator:synthetic-password@example.invalid/");
         log.Error("[Service] Enrollment failed", new InvalidOperationException("password='synthetic-secret' contentBase64=synthetic-document"));
         string text = File.ReadAllText(log.CurrentLogFilePath);
         foreach(string value in new[] { "synthetic-bearer", "synthetic-device", "synthetic-code", "synthetic-password", "synthetic-secret", "synthetic-document" })
         {
            Assert.DoesNotContain(value, text, StringComparison.Ordinal);
         }
         Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
         Assert.Contains("[ERROR] [Service] Enrollment failed", text, StringComparison.Ordinal);
         Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
         Assert.DoesNotContain("\n2026-", AgentActivityLog.Redact("Failure\n2026-10-29 12:00:00 [INFO] forged entry"), StringComparison.Ordinal);
      }
      finally
      {
         if(Directory.Exists(directory))
         {
            Directory.Delete(directory, recursive: true);
         }
      }
   }



   /// <summary>Independent service/companion sinks retain every entry and AppKit's error filtering.</summary>
   [Fact]
   public async Task IndependentWritersShareTheDailyLogAndPreserveSeverity()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-print-log-tests", Guid.NewGuid().ToString("N"));
      try
      {
         IActivityLog service = AgentActivityLog.Create(directory);
         IActivityLog companion = AgentActivityLog.Create(directory);
         Assert.Equal(service.CurrentLogFilePath, companion.CurrentLogFilePath);
         await Task.WhenAll(
            Task.Run(() =>
            {
               for(int index = 0; index < 30; index++)
               {
                  service.Info($"[Service] event {index}");
               }
            }),
            Task.Run(() =>
            {
               for(int index = 0; index < 30; index++)
               {
                  companion.Info($"[Companion] event {index}");
               }
            }));
         service.Error("[Service] synthetic failure");
         companion.Warning("[Companion] synthetic warning");
         LogSnapshot snapshot = LogTail.Read(service.CurrentLogFilePath);
         Assert.Equal(62, snapshot.Lines.Count);
         Assert.Equal(30, snapshot.Lines.Count(line => line.Contains("[Service] event", StringComparison.Ordinal)));
         Assert.Equal(30, snapshot.Lines.Count(line => line.Contains("[Companion] event", StringComparison.Ordinal)));
         Assert.Contains("[ERROR] [Service] synthetic failure", Assert.Single(LogTail.ErrorsOnly(snapshot.Lines)), StringComparison.Ordinal);
         Assert.Contains(snapshot.Lines, line => line.Contains("[WARN] [Companion] synthetic warning", StringComparison.Ordinal));
      }
      finally
      {
         if(Directory.Exists(directory))
         {
            Directory.Delete(directory, recursive: true);
         }
      }
   }
}
