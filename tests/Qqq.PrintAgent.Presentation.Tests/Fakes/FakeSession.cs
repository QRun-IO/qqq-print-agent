/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// A scripted sign-in session that keeps the rules of the real one: a browser sign-in can only be completed after it was
/// begun and only with the callback of the latest attempt, and a credential exists only after a sign-in completed. Each
/// step succeeds unless a failure is set for it, can be held open until the test releases it, and records what the flow
/// passed in. Disposing it behaves like the real client too: a call that is still in flight fails with "disposed" when
/// it finishes, whatever else happened to it, and every later call is refused. A held call resumes on another thread, as
/// the real client's network calls do. It never touches a network.
/// </summary>
internal sealed class FakeSession(Uri origin, bool allowUntrustedCertificate) : ISignInSession
{
   private int _attempts;
   private bool _attemptOpen;
   private bool _signedIn;



   /// <summary>The server the flow asked for.</summary>
   public Uri Origin { get; } = origin;



   /// <summary>Whether the flow asked for the certificate exception when it created this session.</summary>
   internal bool AllowUntrustedCertificate { get; } = allowUntrustedCertificate;



   /// <summary>What discovery returns; a password server by default.</summary>
   internal QqqAuthenticationMetadata Metadata { get; set; } = new() { Type = "TABLE_BASED" };



   /// <summary>When set, discovery does not answer until the test completes it, or until the flow's token is cancelled.</summary>
   internal TaskCompletionSource<QqqAuthenticationMetadata>? PendingDiscovery { get; set; }



   /// <summary>When set, the password sign-in does not answer until the test completes it, or until the flow's token is cancelled.</summary>
   internal TaskCompletionSource? PendingPassword { get; set; }



   /// <summary>When set, discovery fails with this.</summary>
   internal Exception? DiscoverFailure { get; set; }



   /// <summary>When set, the password sign-in fails with this.</summary>
   internal Exception? PasswordFailure { get; set; }



   /// <summary>When set, beginning the browser sign-in fails with this.</summary>
   internal Exception? BeginFailure { get; set; }



   /// <summary>When set, completing the browser sign-in fails with this.</summary>
   internal Exception? CompleteFailure { get; set; }



   /// <summary>When set, exporting the credential fails with this even after a sign-in.</summary>
   internal Exception? ExportFailure { get; set; }



   /// <summary>The user name and password the flow passed to the password sign-in.</summary>
   internal (string Username, string Password)? PasswordAttempt { get; private set; }



   /// <summary>The redirect address the flow began the latest browser sign-in with.</summary>
   internal Uri? Redirect { get; private set; }



   /// <summary>The callback address the flow completed a browser sign-in with.</summary>
   internal Uri? Callback { get; private set; }



   /// <summary>True once the flow discarded this session.</summary>
   internal bool Disposed { get; private set; }



   /// <summary>The provider page of the latest browser attempt; its query stands for that attempt's secret state.</summary>
   internal Uri Authorization => new("https://id.example.com/authorize?state=secret-state-" + _attempts);



   /// <summary>The callback the provider would send the browser to for the latest attempt, carrying that attempt's state and a one-time code.</summary>
   internal Uri CallbackForLatestAttempt => new(SignInFixture.Callback + "?code=one-time-code&state=secret-state-" + _attempts);



   /// <summary>Returns the scripted metadata, waits for the test, or fails.</summary>
   public async Task<QqqAuthenticationMetadata> DiscoverAsync(CancellationToken cancellationToken)
   {
      ObjectDisposedException.ThrowIf(Disposed, this);
      try
      {
         if(DiscoverFailure is not null)
         {
            throw DiscoverFailure;
         }
         return PendingDiscovery is null ? Metadata : await PendingDiscovery.Task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
      }
      finally
      {
         Release();
      }
   }



   /// <summary>Records the credentials and succeeds, waits for the test, or fails as scripted; only success leaves a credential.</summary>
   public async Task SignInWithPasswordAsync(string username, string password, CancellationToken cancellationToken)
   {
      ObjectDisposedException.ThrowIf(Disposed, this);
      PasswordAttempt = (username, password);
      try
      {
         if(PasswordFailure is not null)
         {
            throw PasswordFailure;
         }
         if(PendingPassword is not null)
         {
            await PendingPassword.Task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
         }
         _signedIn = true;
      }
      finally
      {
         Release();
      }
   }



   /// <summary>Starts a new browser attempt with its own state, replacing any earlier one, and returns the provider page; or fails.</summary>
   public Task<Uri> BeginBrowserSignInAsync(Uri redirect, CancellationToken cancellationToken)
   {
      ObjectDisposedException.ThrowIf(Disposed, this);
      Redirect = redirect;
      if(BeginFailure is not null)
      {
         return Task.FromException<Uri>(BeginFailure);
      }
      _attempts++;
      _attemptOpen = true;
      return Task.FromResult(Authorization);
   }



   /// <summary>Completes the open attempt when the callback carries that attempt's state; a callback without a begun attempt, or with another attempt's state, is refused the way the real session refuses it.</summary>
   public Task CompleteBrowserSignInAsync(Uri callback, CancellationToken cancellationToken)
   {
      ObjectDisposedException.ThrowIf(Disposed, this);
      Callback = callback;
      if(!_attemptOpen)
      {
         return Task.FromException(new InvalidOperationException("No browser sign-in was begun on this session."));
      }
      _attemptOpen = false;
      if(CompleteFailure is not null)
      {
         return Task.FromException(CompleteFailure);
      }
      if(!callback.Query.EndsWith("state=secret-state-" + _attempts, StringComparison.Ordinal))
      {
         return Task.FromException(new QqqSessionException(QqqSessionException.FailureKind.InvalidResponse, "The callback does not belong to this sign-in attempt."));
      }
      _signedIn = true;
      return Task.CompletedTask;
   }



   /// <summary>Returns a synthetic session credential for this server once a sign-in completed; before that it fails like the real session, and a scripted failure wins.</summary>
   public QqqSessionCredential ExportCredential()
   {
      if(ExportFailure is not null)
      {
         throw ExportFailure;
      }
      return _signedIn ? new QqqSessionCredential(Origin, "synthetic-session-token")
         : throw new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "Sign in before exporting the session.");
   }



   /// <summary>Marks the session discarded. From then on a call in flight fails as it finishes and a new call is refused, which is what disposing the real client under a running call does.</summary>
   public void Dispose() => Disposed = true;



   /// <summary>
   /// What the real client does at the end of every call: it releases its gate, and a gate that was disposed meanwhile
   /// throws. Because this runs as the call unwinds, it replaces whatever the call was about to return or throw, so a
   /// flow that disposes its session under a running call sees "disposed" instead of the cancellation it asked for.
   /// </summary>
   private void Release() => ObjectDisposedException.ThrowIf(Disposed, this);
}
