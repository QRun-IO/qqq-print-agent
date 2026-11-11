/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// The identity provider's half of a sign-out. The provider is meant to be invisible, so when a server's sign-out
/// comes with the provider's own, that is opened in the browser too: staying signed in to the provider would let the
/// next sign-in skip the credential prompt. This opens the provider's page and waits for the browser to come back
/// with this sign-out's own state, through the one <see cref="BrowserCallbackDispatcher"/>, so it can run while a
/// sign-in to another server waits for its callback. Ending the session in the service is the caller's part. The
/// sign-out request carries the provider's ID token hint: it goes to the browser and nowhere else, and neither it
/// nor its state is logged or kept.
/// </summary>
public sealed class ProviderSignOut
{
   /// <summary>How long the provider is given to confirm its sign-out.</summary>
   public static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

   private readonly IBrowserLauncher _browser;
   private readonly BrowserCallbackDispatcher _dispatcher;
   private readonly IPresentationLog _log;
   private readonly TimeProvider _time;



   /// <summary>Wires the sign-out to the browser, the one callback dispatcher and the log; the time provider drives the limit.</summary>
   public ProviderSignOut(IBrowserLauncher browser, BrowserCallbackDispatcher dispatcher, IPresentationLog log, TimeProvider? time = null)
   {
      _browser = browser ?? throw new ArgumentNullException(nameof(browser));
      _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
      _log = log ?? throw new ArgumentNullException(nameof(log));
      _time = time ?? TimeProvider.System;
   }



   /// <summary>
   /// Opens the provider's end-session page in the default browser and waits up to two minutes for the browser to
   /// return to this application's registered callback with this sign-out's own state. The state is registered
   /// first, which opens the inbox, so a fast redirect is not lost. It returns whether the provider confirmed: false
   /// when it did not come back in time, when the callback with this state is not on the registered address, when
   /// the browser or the inbox failed, and when the caller's token ended the wait. Only the provider's host is
   /// logged. A failure is logged as a warning with its kind and text; the caller's own cancellation is a
   /// deliberate stop, and is logged as information in fixed words.
   /// </summary>
   public async Task<bool> CompleteAsync(QqqProviderLogout logout, CancellationToken cancellationToken = default)
   {
      ArgumentNullException.ThrowIfNull(logout);
      try
      {
         using BrowserCallbackTicket ticket = _dispatcher.Register(logout.State);
         using CancellationTokenSource limit = new(Limit, _time);
         using var wait = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);
         _log.Info("Opening the identity provider's sign-out page in the default browser ({Provider}).", QqqOrigin.ShownAuthority(logout.EndSessionUri));
         BrowserLaunch.Open(_browser, logout.EndSessionUri);
         Uri callback = await ticket.Callback.WaitAsync(wait.Token);
         bool matched = logout.Matches(callback);
         _log.Info(matched ? "Identity provider sign-out callback matched." : "Identity provider sign-out callback did not match this sign-out.");
         return matched;
      }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
         _log.Info("Identity provider sign-out was stopped by its caller before the provider confirmed.");
         return false;
      }
      catch(Exception exception) when(exception is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or ArgumentException)
      {
         _log.Warning("Identity provider sign-out was not confirmed: {Failure}", QqqSessionDiagnostics.Describe(exception));
         return false;
      }
   }
}
