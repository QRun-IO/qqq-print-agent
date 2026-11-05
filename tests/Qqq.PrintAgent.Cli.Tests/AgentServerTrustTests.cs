/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The administrator's certificate exception is refused by default, is bound to one server, persists across store instances, and can be turned off again.</summary>
public sealed class AgentServerTrustTests
{
   /// <summary>A fresh store refuses untrusted certificates; a saved exception is read back by a new store instance and reverts when cleared.</summary>
   [Fact]
   public void ServerTrust_DefaultsToRefuseAndRoundTrips()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-trust-" + Guid.NewGuid().ToString("N"));
      try
      {
         using(AgentStore store = new(directory))
         {
            Assert.False(store.LoadServerTrust().AllowUntrustedCertificate);
            store.SaveServerTrust(new ServerTrust(AllowUntrustedCertificate: true, Origin: "https://127.0.0.1:18581"));
         }
         using(AgentStore store = new(directory, recoverUnreadableConfiguration: true))
         {
            ServerTrust trust = store.LoadServerTrust();
            Assert.True(trust.AllowUntrustedCertificate);
            Assert.Equal("https://127.0.0.1:18581", trust.Origin);
            store.SaveServerTrust(new ServerTrust());
            Assert.False(store.LoadServerTrust().AllowUntrustedCertificate);
         }
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>The exception applies only to the exact scheme, host and port it was saved for (case-insensitively), never to another server, another port or a plain HTTP variant.</summary>
   [Theory]
   [InlineData("https://127.0.0.1:18581/", true)]
   [InlineData("HTTPS://127.0.0.1:18581/qqq/v1/version", true)]
   [InlineData("https://127.0.0.1:18590/", false)]
   [InlineData("http://127.0.0.1:18581/", false)]
   [InlineData("https://print.example.invalid/", false)]
   public void ServerTrust_AppliesOnlyToTheSavedServer(string candidate, bool expected)
   {
      var trust = new ServerTrust(true, ServerTrust.Normalize(new Uri("https://127.0.0.1:18581/anything")));
      Assert.Equal("https://127.0.0.1:18581", trust.Origin);
      Assert.Equal(expected, trust.AppliesTo(new Uri(candidate)));
      Assert.False(new ServerTrust().AppliesTo(new Uri(candidate)));
   }
}
