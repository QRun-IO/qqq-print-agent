/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// Everything a companion sign-in touches, as hand-written fakes around the one real callback dispatcher: scripted
/// sessions, a browser that only records what it was asked to open, callback inboxes a test delivers to by hand, a
/// manual clock and a recording log. Any number of sign-ins and provider sign-outs can be built over one world, which
/// is how a test puts two of them on the one inbox at the same time. Each session gets its own state stem, as two
/// real sessions have different random states. Every value in it is synthetic.
/// </summary>
internal sealed class SignInWorld : ISignInSessionFactory, IBrowserLauncher, IBrowserCallbackSource
{
   /// <summary>The address tests use for the first server.</summary>
   internal const string Server = "https://print.example.com";

   /// <summary>The address tests use for a second server.</summary>
   internal const string OtherServer = "https://labels.example.net";

   /// <summary>The origin of the sign-in provider the fake sessions send the browser to.</summary>
   internal const string Provider = "https://id.example.com";

   /// <summary>The callback the fake installation has registered.</summary>
   internal const string Callback = "io.qrun.qqq-print-agent:/oauth/callback";

   private volatile TaskCompletionSource<FakeInbox>? _awaitedInbox;



   /// <summary>Builds the world with its dispatcher over the fake inboxes.</summary>
   internal SignInWorld()
   {
      Dispatcher = new BrowserCallbackDispatcher(this, Log);
   }



   /// <summary>The one dispatcher every sign-in and sign-out of this world shares.</summary>
   internal BrowserCallbackDispatcher Dispatcher { get; }



   /// <summary>Everything that was logged.</summary>
   internal RecordingLog Log { get; } = new();



   /// <summary>The clock that drives the time limits.</summary>
   internal ManualTime Time { get; } = new();



   /// <summary>Every session that was created, oldest first.</summary>
   internal List<FakeSession> Sessions { get; } = [];



   /// <summary>Applied to each new session before it is used, to script what the server offers or how it fails.</summary>
   internal Action<FakeSession>? ConfigureSession { get; set; }



   /// <summary>When set, creating a session fails with this, as the real client does for an address it refuses.</summary>
   internal Exception? CreateFailure { get; set; }



   /// <summary>Every address the browser was asked to open.</summary>
   internal List<Uri> Opened { get; } = [];



   /// <summary>How many inboxes were open and waiting each time the browser was asked to open an address, which shows that the inbox was open first.</summary>
   internal List<int> ListeningAtLaunch { get; } = [];



   /// <summary>When set, opening the browser fails with this.</summary>
   internal Exception? BrowserFailure { get; set; }



   /// <summary>When set, it runs once the browser has been asked to open an address, on the thread that asked and before that call returns. A test uses it to make something happen between the provider's page opening and the sign-in starting to wait for its callback.</summary>
   internal Action<Uri>? Launched { get; set; }



   /// <summary>Every inbox that was opened, oldest first.</summary>
   internal List<FakeInbox> Inboxes { get; } = [];



   /// <summary>When set, opening an inbox fails with this.</summary>
   internal Exception? InboxFailure { get; set; }



   /// <summary>When set, the inboxes opened from then on answer inline (see <see cref="FakeInbox"/>).</summary>
   internal bool InboxesAnswerInline { get; set; }



   /// <summary>The protocol handler trace; empty means the browser never launched the handler.</summary>
   internal List<string> Trace { get; } = [];



   /// <summary>The window each read of the trace asked for.</summary>
   internal List<TimeSpan> TraceWindows { get; } = [];



   /// <summary>The registered callback.</summary>
   public string RedirectUri => Callback;



   /// <summary>Where the fake protocol handler writes its trace.</summary>
   public string TracePath => "trace/auth-callback.log";



   /// <summary>What the service's check reports for a server that takes a user name and password: that method, and no sign-in provider.</summary>
   internal static ServerCheckResult PasswordCheck { get; } = new([], [ServerCheckResult.Password]);



   /// <summary>What the service's check reports for a server that signs in through the browser at the fake provider.</summary>
   internal static ServerCheckResult BrowserCheck { get; } = new([], [ServerCheckResult.Browser], Provider + "/");



   /// <summary>A sign-in description for a server that signs in through the browser: the native grant with the given issuer and redirect addresses.</summary>
   internal static QqqAuthenticationMetadata BrowserDescription(string issuer = Provider, params string[] redirects) => new()
   {
      Type = "OAUTH2",
      Values = new QqqAuthenticationMetadata.ProviderValues { NativeSessionLogin = new QqqAuthenticationMetadata.NativeGrant { Issuer = issuer, RedirectUris = redirects.Length == 0 ? [Callback] : redirects } },
   };



   /// <summary>A certificate refusal as the session client records one: an untrusted authority, presented by the given origin.</summary>
   internal static QqqCertificateRejection Refusal(string origin) => new(System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors, "CN=synthetic.example", "CN=Synthetic Test Authority",
      new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), "00112233445566778899AABBCCDDEEFF00112233", new Uri(origin), new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero));



   /// <summary>A new sign-in over this world.</summary>
   internal CompanionSignIn NewSignIn() => new(this, this, this, Dispatcher, Log, Time);



   /// <summary>A new sign-in that has read a password server's description and holds its session, with the log cleared so a test asserts only what its own step wrote.</summary>
   internal async Task<CompanionSignIn> PasswordReadyAsync()
   {
      CompanionSignIn signIn = NewSignIn();
      Assert.NotNull(await signIn.PrepareAsync(new Uri(Server), PasswordCheck, QqqCertificateExceptionOrigins.None));
      Log.Entries.Clear();
      return signIn;
   }



   /// <summary>A new sign-in that has read a browser server's description and holds its session, with the log cleared. Each call creates the next session, so two calls give two sign-ins with different states.</summary>
   internal async Task<CompanionSignIn> BrowserReadyAsync(string server = Server)
   {
      ConfigureSession ??= static session => session.Metadata = BrowserDescription();
      CompanionSignIn signIn = NewSignIn();
      Assert.NotNull(await signIn.PrepareAsync(new Uri(server), BrowserCheck, QqqCertificateExceptionOrigins.None));
      Log.Entries.Clear();
      return signIn;
   }



   /// <summary>Records what a sign-in announces from now on, as "Property=value" in order, which is what a view would have been told.</summary>
   internal static List<string> Watch(CompanionSignIn signIn)
   {
      List<string> changes = [];
      signIn.PropertyChanged += (_, change) => changes.Add(change.PropertyName + "=" + (change.PropertyName switch
      {
         nameof(CompanionSignIn.State) => signIn.State.ToString(),
         nameof(CompanionSignIn.IsBusy) => signIn.IsBusy.ToString(),
         nameof(CompanionSignIn.HasSession) => signIn.HasSession.ToString(),
         nameof(CompanionSignIn.Offer) => signIn.Offer?.ToString() ?? "null",
         nameof(CompanionSignIn.Failure) => signIn.Failure ?? "null",
         _ => signIn.Rejection is null ? "null" : "set",
      }));
      return changes;
   }



   /// <summary>
   /// Awaits something that has to end by itself and fails the test, instead of hanging the run, when it does not. The
   /// limit is real time, but it only ever runs out when the code under test is broken; a passing test never waits for it.
   /// </summary>
   internal static async Task<T> EndsAsync<T>(Task<T> operation) => await operation.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);



   /// <summary>
   /// Completes with the next inbox that is opened, once the dispatcher has started waiting on it. The dispatcher
   /// opens the inbox after a callback on another thread's schedule, so a test uses this to hold its next callback
   /// until the new inbox is really open, without sleeping. It must be called before that inbox is opened.
   /// </summary>
   internal async Task<FakeInbox> NextInboxAsync()
   {
      _awaitedInbox = new TaskCompletionSource<FakeInbox>(TaskCreationOptions.RunContinuationsAsynchronously);
      FakeInbox inbox = await _awaitedInbox.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
      await inbox.Listening.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
      return inbox;
   }



   /// <summary>Creates a scripted session with its own state stem, or fails.</summary>
   public ISignInSession Create(Uri origin, QqqCertificateExceptionOrigins certificateException)
   {
      if(CreateFailure is not null)
      {
         throw CreateFailure;
      }
      FakeSession session = new(origin, certificateException) { StateStem = "synthetic-state-" + (char)('a' + Sessions.Count) + "-" };
      ConfigureSession?.Invoke(session);
      Sessions.Add(session);
      return session;
   }



   /// <summary>Records the address instead of opening a browser, then runs what the test asked for at that moment; or fails.</summary>
   public void Open(Uri address)
   {
      if(BrowserFailure is not null)
      {
         throw BrowserFailure;
      }
      ListeningAtLaunch.Add(Inboxes.Count(inbox => inbox.Waiting && !inbox.Disposed));
      Opened.Add(address);
      Launched?.Invoke(address);
   }



   /// <summary>Opens a new inbox the test can deliver to, or fails.</summary>
   IBrowserCallbackInbox IBrowserCallbackSource.Open()
   {
      if(InboxFailure is not null)
      {
         throw InboxFailure;
      }
      FakeInbox inbox = new(InboxesAnswerInline);
      Inboxes.Add(inbox);
      _awaitedInbox?.TrySetResult(inbox);
      return inbox;
   }



   /// <summary>Returns the scripted trace and records the window that was asked for.</summary>
   public IReadOnlyList<string> RecentTrace(TimeSpan window)
   {
      TraceWindows.Add(window);
      return Trace;
   }
}
