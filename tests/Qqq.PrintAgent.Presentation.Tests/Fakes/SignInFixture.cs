/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// A sign-in flow wired to hand-written fakes: a scripted service, scripted sessions, a browser that only records what
/// it was asked to open, callback inboxes a test delivers to, a manual clock and a recording log. It also records every
/// message and stage the flow announced, in order, which is what a view would have shown.
/// </summary>
internal sealed class SignInFixture : ISignInSessionFactory, IBrowserLauncher, IBrowserCallbackSource
{
   /// <summary>The address tests use for the server.</summary>
   internal const string Server = "https://print.example.com";

   /// <summary>The callback the fake installation has registered.</summary>
   internal const string Callback = "io.qrun.qqq-print-agent:/oauth/callback";



   /// <summary>Builds the flow over the fakes and starts recording what it announces.</summary>
   internal SignInFixture()
   {
      Flow = new SignInFlow(new ServiceClient(SendAsync, Log, "QQQ"), this, this, this, Log, Time);
      Flow.PropertyChanged += (_, change) =>
      {
         if(string.Equals(change.PropertyName, nameof(SignInFlow.Feedback), StringComparison.Ordinal))
         {
            Shown.Add(Flow.Feedback!);
         }
         else if(string.Equals(change.PropertyName, nameof(SignInFlow.Stage), StringComparison.Ordinal))
         {
            Stages.Add(Flow.Stage);
         }
         else if(string.Equals(change.PropertyName, nameof(SignInFlow.Trust), StringComparison.Ordinal))
         {
            TrustAnnouncements++;
         }
      };
   }



   /// <summary>The flow under test.</summary>
   internal SignInFlow Flow { get; }



   /// <summary>Everything the flow and its service client logged.</summary>
   internal RecordingLog Log { get; } = new();



   /// <summary>The clock that drives the browser time limits.</summary>
   internal ManualTime Time { get; } = new();



   /// <summary>Every message the flow announced, oldest first.</summary>
   internal List<FeedbackMessage> Shown { get; } = [];



   /// <summary>Every stage the flow moved to, oldest first.</summary>
   internal List<SignInStage> Stages { get; } = [];



   /// <summary>How many times the flow announced the certificate exception, which is when a view re-applies its checkbox.</summary>
   internal int TrustAnnouncements { get; private set; }



   /// <summary>Every request the flow sent to the service.</summary>
   internal List<PipeRequest> Requests { get; } = [];



   /// <summary>How the scripted service answers; by default it confirms a sign-in.</summary>
   internal Func<PipeRequest, Task<string>> Service { get; set; } = _ => Task.FromResult("Signed in to QQQ. Printing is ready.");



   /// <summary>Every session the flow created, oldest first.</summary>
   internal List<FakeSession> Sessions { get; } = [];



   /// <summary>Applied to each new session before the flow uses it, to script what the server offers or how it fails.</summary>
   internal Action<FakeSession>? ConfigureSession { get; set; }



   /// <summary>When set, creating a session fails with this, as the real client does for an address it refuses.</summary>
   internal Exception? CreateFailure { get; set; }



   /// <summary>Every address the flow asked the browser to open.</summary>
   internal List<Uri> Opened { get; } = [];



   /// <summary>When set, opening the browser fails with this.</summary>
   internal Exception? BrowserFailure { get; set; }



   /// <summary>Every inbox the flow opened, oldest first.</summary>
   internal List<FakeInbox> Inboxes { get; } = [];



   /// <summary>When set, opening the inbox fails with this.</summary>
   internal Exception? InboxFailure { get; set; }



   /// <summary>The protocol handler trace the flow finds; empty means the browser never launched the handler.</summary>
   internal List<string> Trace { get; } = [];



   /// <summary>The registered callback.</summary>
   public string RedirectUri => Callback;



   /// <summary>Where the fake protocol handler writes its trace.</summary>
   public string TracePath => "trace/auth-callback.log";



   /// <summary>The titles of the messages announced so far, joined for a compact assertion.</summary>
   internal string Titles => string.Join(" > ", Shown.Select(message => message.Title));



   /// <summary>A server description offering the native browser grant with the given redirect addresses, and password sign-in when asked.</summary>
   internal static QqqAuthenticationMetadata Offers(bool password, params string[] redirects) => new()
   {
      Type = password ? "TABLE_BASED" : "OAUTH2",
      Values = new QqqAuthenticationMetadata.ProviderValues { NativeSessionLogin = new QqqAuthenticationMetadata.NativeGrant { RedirectUris = redirects } },
   };



   /// <summary>Records the request and answers as scripted.</summary>
   private Task<string> SendAsync(PipeRequest request)
   {
      Requests.Add(request);
      return Service(request);
   }



   /// <summary>Creates a scripted session or fails.</summary>
   public ISignInSession Create(Uri origin, bool allowUntrustedCertificate)
   {
      if(CreateFailure is not null)
      {
         throw CreateFailure;
      }
      FakeSession session = new(origin, allowUntrustedCertificate);
      ConfigureSession?.Invoke(session);
      Sessions.Add(session);
      return session;
   }



   /// <summary>Records the address instead of opening a browser, or fails.</summary>
   public void Open(Uri address)
   {
      if(BrowserFailure is not null)
      {
         throw BrowserFailure;
      }
      Opened.Add(address);
   }



   /// <summary>Opens a new inbox the test can deliver to, or fails.</summary>
   IBrowserCallbackInbox IBrowserCallbackSource.Open()
   {
      if(InboxFailure is not null)
      {
         throw InboxFailure;
      }
      FakeInbox inbox = new();
      Inboxes.Add(inbox);
      return inbox;
   }



   /// <summary>Returns the scripted trace, whatever window is asked for.</summary>
   public IReadOnlyList<string> RecentTrace(TimeSpan window) => Trace;
}
