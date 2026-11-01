/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class BrowserCallbackTests
{
   [Fact]
   public void CallbackCompanionFollowsTheInstalledCliRatherThanDefaultBrandFolder()
   {
      string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Custom connector");
      Assert.Equal(Path.Combine(root, "App", "Qqq.PrintAgent.exe"), QqqBrowserCallback.CompanionSibling(Path.Combine(root, "Cli", "qqq-print-agent.exe")));
      Assert.Throws<UnauthorizedAccessException>(() => QqqBrowserCallback.CompanionSibling(Path.Combine(root, "qqq-print-agent.exe")));
   }

   [Theory]
   [InlineData("https://example.invalid/oauth/callback?code=x&state=y")]
   [InlineData("io.qrun.qqq-print-agent:/other?code=x&state=y")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?code=x#fragment")]
   public void InvalidCallbacksNeverReachAnInbox(string callback)
      => Assert.Throws<InvalidDataException>(() => QqqBrowserCallback.Validate(callback));

   [Fact]
   public void OverlongCallbackIsRejected()
      => Assert.Throws<InvalidDataException>(() => QqqBrowserCallback.Validate(QqqBrowserCallback.RedirectUri + "?code=" + new string('a', 8192)));

   [Fact]
   public async Task NoActiveAttemptFailsWithoutLaunchingACompanion()
   {
      using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(100));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QqqBrowserCallback.ForwardAsync(
         QqqBrowserCallback.RedirectUri + "?code=synthetic&state=synthetic", "qqq-no-inbox-" + Guid.NewGuid().ToString("N"), Environment.ProcessPath!, timeout.Token));
   }

   [Fact]
   public async Task ActiveSameSessionInboxReceivesOneBoundedCallback()
   {
      string name = "qqq-callback-test-" + Guid.NewGuid().ToString("N");
      using QqqBrowserCallback inbox = new(name);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      Task<Uri> received = inbox.ReceiveAsync(timeout.Token);
      string callback = QqqBrowserCallback.RedirectUri + "?code=synthetic&state=synthetic";
      await QqqBrowserCallback.ForwardAsync(callback, name, Environment.ProcessPath!, timeout.Token);
      Assert.Equal(callback, (await received).AbsoluteUri);
   }

   [Fact]
   public async Task WrongCompanionExecutableReceivesNoCallbackBytes()
   {
      string name = "qqq-callback-test-" + Guid.NewGuid().ToString("N");
      using QqqBrowserCallback inbox = new(name);
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      Task<Uri> received = inbox.ReceiveAsync(timeout.Token);
      await Assert.ThrowsAsync<UnauthorizedAccessException>(() => QqqBrowserCallback.ForwardAsync(
         QqqBrowserCallback.RedirectUri + "?code=synthetic&state=synthetic", name, "C:\\not-the-installed-companion.exe", timeout.Token));
      await Assert.ThrowsAsync<IOException>(() => received);
   }
}
