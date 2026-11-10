/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.SignIn;

/// <summary>Enrollment with a code, and the certificate exception for one development server.</summary>
public sealed class SignInFlowEnrollmentTests
{
   /// <summary>An HTTPS address, or plain HTTP to this computer itself, together with a code is accepted without any message.</summary>
   [Theory]
   [InlineData("https://print.example.com", "code-123")]
   [InlineData("  https://print.example.com/base/  ", "code-123")]
   [InlineData("http://localhost:8080", "code-123")]
   [InlineData("http://127.0.0.1:18581", " code ")]
   public void CompleteForm_IsAccepted(string address, string code)
   {
      SignInFixture fixture = new();

      Assert.True(fixture.Flow.CheckEnrollment(address, code));
      Assert.Empty(fixture.Shown);
   }



   /// <summary>Plain HTTP to another host, an address with user information, a query or a fragment, something that is not an address, and a missing code are all refused before anything is sent, with one message saying what is needed.</summary>
   [Theory]
   [InlineData("http://print.example.com", "code-123")]
   [InlineData("https://operator@print.example.com", "code-123")]
   [InlineData("https://print.example.com/?tenant=1", "code-123")]
   [InlineData("https://print.example.com/#top", "code-123")]
   [InlineData("ftp://print.example.com", "code-123")]
   [InlineData("print.example.com", "code-123")]
   [InlineData(null, "code-123")]
   [InlineData("https://print.example.com", "   ")]
   [InlineData("https://print.example.com", null)]
   public void IncompleteOrUnsafeForm_IsRefusedWithGuidance(string? address, string? code)
   {
      SignInFixture fixture = new();

      Assert.False(fixture.Flow.CheckEnrollment(address, code));
      Assert.Equal(new FeedbackMessage("Check the connection details", "Enter an HTTPS server address and the enrollment code from the server. Localhost HTTP is supported for development.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.Empty(fixture.Requests);
   }



   /// <summary>A successful enrollment sends the address without surrounding spaces or a trailing slash and the trimmed code, shows the next steps, returns the service's confirmation as the status line, and logs that enrollment completed without the code. The flow is in its enrolling stage meanwhile and returns to where it was.</summary>
   [Fact]
   public async Task AcceptedCode_ConnectsTheDevice()
   {
      SignInFixture fixture = new() { Service = _ => Task.FromResult("Enrolled device device-42") };

      EnrollmentResult result = await fixture.Flow.EnrollAsync(" https://print.example.com/ ", " code-123 ");

      Assert.Equal(new EnrollmentResult("Enrolled device device-42", ServiceReachable: true), result);
      PipeRequest request = Assert.Single(fixture.Requests);
      Assert.Equal("enroll", request.Operation);
      Assert.Equal("https://print.example.com", request.ServerUrl);
      Assert.Equal("code-123", request.EnrollmentCode);
      Assert.Equal([new FeedbackMessage("Connecting", "The service is enrolling this device with the server…", FeedbackSeverity.Informational),
         new FeedbackMessage("Device connected", "Enrolled device device-42. Select which printers to expose here, then approve the discovered queues in the server.", FeedbackSeverity.Success)], fixture.Shown);
      Assert.Equal("INFO Print device enrollment completed.", Assert.Single(fixture.Log.Entries));
      Assert.Equal([SignInStage.Enrolling, SignInStage.Idle], fixture.Stages);
      Assert.False(fixture.Flow.IsBusy);
   }



   /// <summary>Enrolling does not disturb a discovered sign-in: the session, the offered methods and the stage are as they were afterwards.</summary>
   [Fact]
   public async Task Enrollment_LeavesADiscoveredSignInAsItWas()
   {
      SignInFixture fixture = new();
      await fixture.Flow.DiscoverAsync(SignInFixture.Server);
      fixture.Service = _ => Task.FromResult("Enrolled device device-42");

      _ = await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");

      Assert.Equal(SignInStage.Ready, fixture.Flow.Stage);
      Assert.True(fixture.Flow.HasSession);
      Assert.True(fixture.Flow.PasswordOffered);
   }



   /// <summary>A refused enrollment shows the service's reply whole, followed by what to do and that the previous connection is kept.</summary>
   [Fact]
   public async Task RefusedCode_ShowsTheServiceReplyAndWhatToDo()
   {
      SignInFixture fixture = new() { Service = _ => Task.FromResult("Error: The enrollment code was already used.") };

      EnrollmentResult result = await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");

      Assert.Equal(new EnrollmentResult(null, ServiceReachable: true), result);
      Assert.Equal(new FeedbackMessage("Enrollment failed", "Error: The enrollment code was already used. If the code expired or was already used, generate a fresh code in the server. Your previous connection is retained unless enrollment completes.", FeedbackSeverity.Error), fixture.Shown[^1]);
   }



   /// <summary>
   /// An enrollment that left this computer without a usable answer (the reply time limit, a pipe that closed mid-reply, an
   /// oversize reply) is not called a failure: the code may have been used, so the operator is told to check the server before
   /// trying again, and the service is not reported as unreachable. The message starts with what really happened, so it
   /// says "timed out" only for a time limit.
   /// </summary>
   [Theory]
   [MemberData(nameof(UnconfirmedFailures))]
   public async Task EnrollmentThatLeftWithoutAnAnswer_AsksToCheckTheServerFirst(Exception failure, string cause)
   {
      SignInFixture fixture = new() { Service = _ => Task.FromException<string>(failure) };

      EnrollmentResult result = await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");

      Assert.Equal(new EnrollmentResult(null, ServiceReachable: true), result);
      Assert.Equal(new FeedbackMessage("Enrollment could not be confirmed", cause + " Check the device list in the server before trying again: the code may have been consumed. Revoke any unused device created by this attempt, then generate a fresh enrollment code. Your prior connection remains available unless this enrollment completed.", FeedbackSeverity.Warning), fixture.Shown[^1]);
   }



   /// <summary>The failures after which the code may have been used, and the sentence that says what happened for each.</summary>
   public static TheoryData<Exception, string> UnconfirmedFailures() => new()
   {
      { new OperationCanceledException("The reply did not arrive in time."), "The request timed out." },
      { new IOException("Service connection closed before a complete response was received."), "The connection to the service closed before its answer arrived." },
      { new InvalidDataException("Local pipe message exceeds the permitted size."), "The service's answer was larger than the companion accepts." },
   };



   /// <summary>Cancelling the wait for an enrollment that was sent is unconfirmed too: the service may still enroll the device, so the same check-the-server message is shown, and it says the wait was ended instead of claiming a time limit.</summary>
   [Fact]
   public async Task CancelledEnrollment_IsUnconfirmed()
   {
      SignInFixture fixture = new();
      (Task reached, _) = fixture.HoldService();
      using CancellationTokenSource cancellation = new();

      Task<EnrollmentResult> running = fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123", cancellation.Token);
      await reached;
      Assert.Equal(SignInStage.Enrolling, fixture.Flow.Stage);
      await cancellation.CancelAsync();

      Assert.Equal(new EnrollmentResult(null, ServiceReachable: true), await SignInFixture.EndsAsync(running));
      Assert.Equal("Connecting > Enrollment could not be confirmed", fixture.Titles);
      Assert.StartsWith("The wait for the service's answer was ended. Check the device list in the server before trying again: the code may have been consumed.", fixture.Shown[^1].Message, StringComparison.Ordinal);
   }



   /// <summary>
   /// Disposing the flow while the service is enrolling ends the wait: the enrollment ends by itself, quietly, without
   /// telling a closed window that the enrollment could not be confirmed, and announces nothing more.
   /// </summary>
   [Fact]
   public async Task DisposedDuringEnrollment_EndsQuietly()
   {
      SignInFixture fixture = new();
      (Task reached, _) = fixture.HoldService();

      Task<EnrollmentResult> running = fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");
      await reached;
      int announced = fixture.Changes.Count;
      fixture.Flow.Dispose();

      Assert.Equal(new EnrollmentResult(null, ServiceReachable: true), await SignInFixture.EndsAsync(running));
      Assert.Equal("Connecting", fixture.Titles);
      Assert.Equal(announced, fixture.Changes.Count);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal("DEBUG Enrollment ended because the connection flow was closed (OperationCanceledException).", fixture.Log.Entries[^1]);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.StartsWith("ERROR", StringComparison.Ordinal));
   }



   /// <summary>
   /// When the request never left this computer (nothing is listening, the pipe was refused, the service was not verified), the
   /// code was not used: the flow reports that to the caller and shows nothing more itself, so the window shows its
   /// service-unavailable state instead of a warning about a consumed code.
   /// </summary>
   [Theory]
   [MemberData(nameof(NotSentFailures))]
   public async Task EnrollmentThatNeverLeft_IsLeftToTheCallerAsAnUnreachableService(Exception failure)
   {
      SignInFixture fixture = new() { Service = _ => Task.FromException<string>(failure) };

      EnrollmentResult result = await fixture.Flow.EnrollAsync(SignInFixture.Server, "code-123");

      Assert.Equal(new EnrollmentResult(null, ServiceReachable: false), result);
      Assert.Equal("Connecting", Assert.Single(fixture.Shown).Title);
   }



   /// <summary>The failures after which the code was certainly not used.</summary>
   public static TheoryData<Exception> NotSentFailures() => new()
   {
      new AgentPipeNotSentException(new OperationCanceledException("Nothing is listening on the pipe.")),
      new AgentPipeNotSentException(new IOException("The pipe could not be opened.")),
      new AgentPipeNotSentException(new UnauthorizedAccessException("The local endpoint is not the installed Windows print service.")),
   };



   /// <summary>An enrollment without an address or a code is a programming error, since the form is checked first; it is refused before the flow is claimed.</summary>
   [Fact]
   public async Task EnrollmentWithoutItsValues_IsRefused()
   {
      SignInFixture fixture = new();

      await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Flow.EnrollAsync(null!, "code-123"));
      await Assert.ThrowsAsync<ArgumentNullException>(() => fixture.Flow.EnrollAsync(SignInFixture.Server, null!));
      Assert.False(fixture.Flow.IsBusy);
   }



   /// <summary>Reading the saved exception shows it and names its server in the checkbox label; with none saved the label speaks of "this server".</summary>
   [Fact]
   public async Task SavedException_IsShownWithItsServer()
   {
      SignInFixture fixture = new() { Service = _ => Task.FromResult("""{"allowUntrustedCertificate":true,"origin":"https://dev.example.com:8443"}""") };

      await fixture.Flow.LoadTrustAsync();

      Assert.Equal(new ServerTrust(true, "https://dev.example.com:8443"), fixture.Flow.Trust);
      Assert.Equal("get-server-trust", Assert.Single(fixture.Requests).Operation);
      Assert.Equal(1, fixture.TrustAnnouncements);
      Assert.False(fixture.Flow.IsBusy);
      Assert.Equal("Allow untrusted server certificate for https://dev.example.com:8443 (development only)", SignInMessages.TrustLabel(fixture.Flow.Trust));
      Assert.Equal("Allow untrusted server certificate for this server (development only)", SignInMessages.TrustLabel(new ServerTrust()));
   }



   /// <summary>When the saved exception cannot be read, because the service refuses or cannot be reached, none is assumed and the reason is logged.</summary>
   [Theory]
   [InlineData(true, "WARN Could not read the server trust setting: Error: Access denied.")]
   [InlineData(false, "ERROR Could not read the server trust setting from the service: Service connection closed.")]
   public async Task UnreadableException_MeansNoException(bool answered, string expectedLog)
   {
      SignInFixture fixture = new();
      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com"));
      fixture.Service = _ => answered ? Task.FromResult("Error: Access denied.") : Task.FromException<string>(new IOException("Service connection closed."));

      await fixture.Flow.LoadTrustAsync();

      Assert.Equal(new ServerTrust(), fixture.Flow.Trust);
      Assert.Contains(expectedLog, fixture.Log.Entries, StringComparer.Ordinal);
      Assert.Equal(answered, !fixture.Log.Entries.Any(entry => entry.StartsWith("DEBUG Service request 'get-server-trust' failure detail: System.IO.IOException", StringComparison.Ordinal)));
   }



   /// <summary>Disposing the flow while the exception is being read or saved ends the wait and keeps the exception the flow already held. Nothing is announced to the closed window, not even the checkbox going back, a save in flight is not reported as an unreachable service, and nothing is logged as a warning or an error.</summary>
   [Fact]
   public async Task DisposedWhileReadingOrSaving_KeepsWhatWasHeldAndAnnouncesNothing()
   {
      SignInFixture reading = new();
      (Task readReached, _) = reading.HoldService();
      Task load = reading.Flow.LoadTrustAsync();
      await readReached;
      reading.Forget();
      reading.Flow.Dispose();
      await load.WaitAsync(TimeSpan.FromSeconds(10));

      SignInFixture saving = new();
      (Task saveReached, _) = saving.HoldService();
      Task<bool> save = saving.Flow.SetTrustAsync(allow: true, "https://dev.example.com");
      await saveReached;
      saving.Forget();
      saving.Flow.Dispose();

      Assert.True(await SignInFixture.EndsAsync(save));
      Assert.Empty(reading.Changes);
      Assert.Equal(new ServerTrust(), reading.Flow.Trust);
      Assert.Equal("DEBUG Reading the certificate exception ended because the connection flow was closed (OperationCanceledException).", reading.Log.Entries[^1]);
      Assert.Empty(saving.Changes);
      Assert.Equal(new ServerTrust(), saving.Flow.Trust);
      Assert.Equal("DEBUG Saving the certificate exception ended because the connection flow was closed (OperationCanceledException).", saving.Log.Entries[^1]);
      Assert.DoesNotContain(reading.Log.Entries.Concat(saving.Log.Entries), entry => entry.StartsWith("WARN", StringComparison.Ordinal) || entry.StartsWith("ERROR", StringComparison.Ordinal));
      Assert.False(reading.Flow.IsBusy);
      Assert.False(saving.Flow.IsBusy);
   }



   /// <summary>Allowing needs a server address: without one nothing is sent, the saved state is announced again so the checkbox goes back, and the operator is told why.</summary>
   [Theory]
   [InlineData("")]
   [InlineData(null)]
   [InlineData("dev.example.com")]
   public async Task AllowingWithoutAnAddress_IsRefused(string? address)
   {
      SignInFixture fixture = new();

      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, address));

      Assert.Empty(fixture.Requests);
      Assert.Equal(new ServerTrust(), fixture.Flow.Trust);
      Assert.Equal(1, fixture.TrustAnnouncements);
      Assert.Equal(new FeedbackMessage("Enter the server address first", "The certificate exception is saved for one server only, so the server address must be entered before it can be allowed.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
   }



   /// <summary>Allowing saves the exception for the server's scheme, host and port only, shows a warning that names it, and logs the change as a warning.</summary>
   [Fact]
   public async Task Allowing_SavesTheExceptionForOneOrigin()
   {
      SignInFixture fixture = new();

      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, "  https://Dev.Example.com:8443/qqq/path  "));

      PipeRequest request = Assert.Single(fixture.Requests);
      Assert.Equal("set-server-trust", request.Operation);
      Assert.Equal(new ServerTrust(true, "https://dev.example.com:8443"), request.ServerTrust);
      Assert.Equal(new ServerTrust(true, "https://dev.example.com:8443"), fixture.Flow.Trust);
      Assert.Equal(new FeedbackMessage("Untrusted certificate allowed for https://dev.example.com:8443", "The service and companion will accept any certificate from this one server until this is turned off. Other servers keep normal validation. Use only with a development server.", FeedbackSeverity.Warning), Assert.Single(fixture.Shown));
      Assert.Equal("WARN Untrusted server certificates ALLOWED by the operator for https://dev.example.com:8443 (development only).", Assert.Single(fixture.Log.Entries));
   }



   /// <summary>Turning the exception off saves an empty exception, whatever is in the address box, and says normal validation is back.</summary>
   [Fact]
   public async Task Refusing_RemovesTheException()
   {
      SignInFixture fixture = new();
      Assert.True(await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com"));

      Assert.True(await fixture.Flow.SetTrustAsync(allow: false, "not an address"));

      Assert.Equal(new ServerTrust(), fixture.Requests[1].ServerTrust);
      Assert.Equal(new ServerTrust(), fixture.Flow.Trust);
      Assert.Equal(new FeedbackMessage("Untrusted certificates refused", "Only certificates Windows trusts for the service account are accepted again.", FeedbackSeverity.Informational), fixture.Shown[^1]);
      Assert.Contains("INFO Untrusted server certificates refused again for every server (default).", fixture.Log.Entries, StringComparer.Ordinal);
   }



   /// <summary>When the service refuses to save, its reply is shown and the saved state is announced again unchanged; when it gives no usable answer, the caller is told and the reason is logged with the full exception as debug detail.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public async Task UnsavedException_LeavesTheSavedStateInForce(bool answered)
   {
      SignInFixture fixture = new() { Service = _ => answered ? Task.FromResult("Error: Only an administrator may change this.") : Task.FromException<string>(new IOException("Service connection closed.")) };

      bool reachable = await fixture.Flow.SetTrustAsync(allow: true, "https://dev.example.com");

      Assert.Equal(answered, reachable);
      Assert.Equal(new ServerTrust(), fixture.Flow.Trust);
      Assert.Equal(1, fixture.TrustAnnouncements);
      Assert.False(fixture.Flow.IsBusy);
      if(answered)
      {
         Assert.Equal(new FeedbackMessage("Server trust not saved", "Error: Only an administrator may change this.", FeedbackSeverity.Error), Assert.Single(fixture.Shown));
      }
      else
      {
         Assert.Empty(fixture.Shown);
         Assert.Contains("ERROR Server trust setting could not be saved: Service connection closed.", fixture.Log.Entries, StringComparer.Ordinal);
         Assert.Contains(fixture.Log.Entries, entry => entry.StartsWith("DEBUG Service request 'set-server-trust' failure detail: System.IO.IOException", StringComparison.Ordinal));
      }
   }
}
