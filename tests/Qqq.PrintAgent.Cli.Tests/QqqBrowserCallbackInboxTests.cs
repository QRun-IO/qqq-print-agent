/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The browser callback inbox must be reachable by the protocol handler the browser launches at medium integrity even when the
/// companion runs elevated: the pipe is owned by the user, restricted to the user, labelled medium integrity, and a forwarded
/// callback from a same-user process is received and acknowledged.
/// </summary>
public sealed class QqqBrowserCallbackInboxTests
{
   /// <summary>The inbox descriptor names the current user (not the token's default owner) as owner and grants access to that user's SID only, so an elevated companion cannot lock out the unelevated handler and no other account can reach the inbox.</summary>
   [Fact]
   public void InboxSecurity_IsUserOwnedUserOnlyAndMediumIntegrity()
   {
      using var identity = WindowsIdentity.GetCurrent();
      PipeSecurity security = QqqBrowserCallback.InboxSecurity();
      Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
      AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
      AuthorizationRule rule = Assert.Single(rules.Cast<AuthorizationRule>());
      Assert.Equal(identity.User, rule.IdentityReference);
      Assert.Equal(AccessControlType.Allow, ((PipeAccessRule)rule).AccessControlType);
      Assert.Equal(PipeAccessRights.FullControl, ((PipeAccessRule)rule).PipeAccessRights);
   }



   /// <summary>
   /// The pipe the companion actually creates is owned by the user's SID, not by the token's default owner. On an elevated
   /// process (hosted CI runs elevated) the default owner is the Administrators group, so this fails there if the inbox ever
   /// goes back to PipeOptions.CurrentUserOnly, which is exactly the regression that locked out the browser-launched handler.
   /// </summary>
   [Fact]
   public void CreatedInbox_IsOwnedByTheUserNotTheTokenDefaultOwner()
   {
      using var identity = WindowsIdentity.GetCurrent();
      using QqqBrowserCallback inbox = new("QqqPrintAgent.Auth.Test." + Guid.NewGuid().ToString("N"));
      PipeSecurity live = inbox.CurrentSecurity();
      Assert.Equal(identity.User, live.GetOwner(typeof(SecurityIdentifier)));
      if(identity.Owner is not null && identity.Owner != identity.User)
      {
         Assert.NotEqual(identity.Owner, live.GetOwner(typeof(SecurityIdentifier)));
      }
      foreach(AuthorizationRule rule in live.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
      {
         Assert.Equal(identity.User, rule.IdentityReference);
      }
   }



   /// <summary>A callback forwarded by a same-user process through the handler's own client path reaches the inbox, is validated, acknowledged, and returned to the waiting sign-in.</summary>
   [Fact]
   public async Task ForwardedCallback_ReachesTheInboxAndIsAcknowledged()
   {
      string name = "QqqPrintAgent.Auth.Test." + Guid.NewGuid().ToString("N");
      using QqqBrowserCallback inbox = new(name);
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
      Task<Uri> receiving = inbox.ReceiveAsync(timeout.Token);
      var steps = new List<string>();
      await QqqBrowserCallback.ForwardAsync(QqqBrowserCallback.RedirectUri + "?code=test-code&state=test-state", name, Environment.ProcessPath!, timeout.Token, steps.Add);
      Uri received = await receiving;
      Assert.Equal("?code=test-code&state=test-state", received.Query);
      Assert.Contains(steps, step => step.StartsWith("Companion verified", StringComparison.Ordinal));
      Assert.DoesNotContain(steps, step => step.Contains("test-code", StringComparison.Ordinal));
   }



   /// <summary>An address that is not this application's redirect, or that carries no query, is refused before anything connects.</summary>
   [Theory]
   [InlineData("io.qrun.qqq-print-agent:/oauth/callback")]
   [InlineData("io.qrun.qqq-print-agent:/other?code=x")]
   [InlineData("https://evil.invalid/oauth/callback?code=x")]
   public void InvalidCallback_IsRefused(string callback)
      => Assert.Throws<InvalidDataException>(() => QqqBrowserCallback.Validate(callback));
}
