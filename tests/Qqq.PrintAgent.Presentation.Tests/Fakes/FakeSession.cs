/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>A scripted sign-in session: each step succeeds unless a failure is set for it, and what the flow passed in is recorded so a test can assert it. It never touches a network.</summary>
internal sealed class FakeSession(Uri origin, bool allowUntrustedCertificate) : ISignInSession
{
   /// <summary>The server the flow asked for.</summary>
   public Uri Origin { get; } = origin;



   /// <summary>Whether the flow asked for the certificate exception when it created this session.</summary>
   internal bool AllowUntrustedCertificate { get; } = allowUntrustedCertificate;



   /// <summary>What discovery returns; a password server by default.</summary>
   internal QqqAuthenticationMetadata Metadata { get; set; } = new() { Type = "TABLE_BASED" };



   /// <summary>When set, discovery fails with this.</summary>
   internal Exception? DiscoverFailure { get; set; }



   /// <summary>When set, the password sign-in fails with this.</summary>
   internal Exception? PasswordFailure { get; set; }



   /// <summary>When set, beginning the browser sign-in fails with this.</summary>
   internal Exception? BeginFailure { get; set; }



   /// <summary>When set, completing the browser sign-in fails with this.</summary>
   internal Exception? CompleteFailure { get; set; }



   /// <summary>When set, exporting the credential fails with this, as it does when no sign-in completed.</summary>
   internal Exception? ExportFailure { get; set; }



   /// <summary>The provider page the browser sign-in asks the flow to open; its query stands for the secret state.</summary>
   internal Uri Authorization { get; set; } = new("https://id.example.com/authorize?state=secret-state");



   /// <summary>The user name and password the flow passed to the password sign-in.</summary>
   internal (string Username, string Password)? PasswordAttempt { get; private set; }



   /// <summary>The redirect address the flow began the browser sign-in with.</summary>
   internal Uri? Redirect { get; private set; }



   /// <summary>The callback address the flow completed the browser sign-in with.</summary>
   internal Uri? Callback { get; private set; }



   /// <summary>True once the flow discarded this session.</summary>
   internal bool Disposed { get; private set; }



   /// <summary>Returns the scripted metadata or fails.</summary>
   public Task<QqqAuthenticationMetadata> DiscoverAsync(CancellationToken cancellationToken)
      => DiscoverFailure is null ? Task.FromResult(Metadata) : Task.FromException<QqqAuthenticationMetadata>(DiscoverFailure);



   /// <summary>Records the credentials and succeeds or fails as scripted.</summary>
   public Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken)
   {
      PasswordAttempt = (username, password);
      return PasswordFailure is null ? Task.CompletedTask : Task.FromException(PasswordFailure);
   }



   /// <summary>Records the redirect and returns the provider page or fails.</summary>
   public Task<Uri> BeginBrowserSignInAsync(Uri redirect, CancellationToken cancellationToken)
   {
      Redirect = redirect;
      return BeginFailure is null ? Task.FromResult(Authorization) : Task.FromException<Uri>(BeginFailure);
   }



   /// <summary>Records the callback and succeeds or fails as scripted.</summary>
   public Task CompleteBrowserSignInAsync(Uri callback, CancellationToken cancellationToken)
   {
      Callback = callback;
      return CompleteFailure is null ? Task.CompletedTask : Task.FromException(CompleteFailure);
   }



   /// <summary>Returns a synthetic session credential for this server or fails.</summary>
   public QqqSessionCredential ExportCredential() => ExportFailure is null ? new QqqSessionCredential(Origin, "synthetic-session-token") : throw ExportFailure;



   /// <summary>Marks the session discarded.</summary>
   public void Dispose() => Disposed = true;
}
