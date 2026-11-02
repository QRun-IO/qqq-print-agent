/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The browser sign-in returns to the machine through a custom URL that Windows hands to the CLI, which forwards it over a local
/// pipe to the running companion. These tests protect that hand-off: only a well-formed callback is forwarded, only to the
/// installed companion, and only while a sign-in is actually waiting.
/// </summary>
public sealed class BrowserCallbackTests
{
   /// <summary>The companion is found next to the CLI that is actually running (App beside Cli under whatever install folder was used), not at the default product folder; a CLI outside that layout is refused.</summary>
   [Fact]
   public void CallbackCompanionFollowsTheInstalledCliRatherThanDefaultBrandFolder()
   {
      string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Custom connector");
      Assert.Equal(Path.Combine(root, "App", "Qqq.PrintAgent.exe"), QqqBrowserCallback.CompanionSibling(Path.Combine(root, "Cli", "qqq-print-agent.exe")));
      Assert.Throws<UnauthorizedAccessException>(() => QqqBrowserCallback.CompanionSibling(Path.Combine(root, "qqq-print-agent.exe")));
   }



   /// <summary>A callback with the wrong scheme, the wrong path, no query, or a fragment is rejected before any pipe is opened, so nothing but a genuine sign-in redirect can reach the companion.</summary>
   [Theory]
   [InlineData("https://example.invalid/oauth/callback?code=x&state=y")]
   [InlineData("io.qrun.qqq-print-agent:/other?code=x&state=y")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback")]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback?code=x#fragment")]
   public void InvalidCallbacksNeverReachAnInbox(string callback)
      => Assert.Throws<InvalidDataException>(() => QqqBrowserCallback.Validate(callback));



   /// <summary>A callback carrying 8 KiB of query text is rejected, bounding what an outside caller can push through the local pipe.</summary>
   [Fact]
   public void OverlongCallbackIsRejected()
      => Assert.Throws<InvalidDataException>(() => QqqBrowserCallback.Validate(QqqBrowserCallback.RedirectUri + "?code=" + new string('a', 8192)));



   /// <summary>When no companion is waiting for a callback, forwarding fails (here by timeout) instead of starting a new companion, so a stray or stale redirect cannot launch software.</summary>
   [Fact]
   public async Task NoActiveAttemptFailsWithoutLaunchingACompanion()
   {
      using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(100));
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => QqqBrowserCallback.ForwardAsync(
         QqqBrowserCallback.RedirectUri + "?code=synthetic&state=synthetic", "qqq-no-inbox-" + Guid.NewGuid().ToString("N"), Environment.ProcessPath!, timeout.Token));
   }



   /// <summary>A companion waiting on its private pipe in the same Windows session receives exactly the callback that was forwarded, unchanged.</summary>
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



   /// <summary>If the process on the pipe is not the installed companion executable, forwarding is refused and the listener receives no bytes at all, so the one-time sign-in code is never handed to an impostor.</summary>
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
