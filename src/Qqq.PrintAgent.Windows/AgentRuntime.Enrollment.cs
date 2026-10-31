using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   private async Task<string> EnrollNativeAsync(PipeRequest request, CancellationToken token)
   {
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         IQqqSessionClient session = _sessionClient ?? throw new InvalidOperationException("Sign in to QQQ before enrolling this computer.");
         if(!session.IsAuthenticated || request.ServerUrl is null || !SameOrigin(session.Origin, request.ServerUrl))
         {
            throw new InvalidOperationException("Sign in to the selected QQQ server before enrollment.");
         }
         QqqSessionBinding identity = await session.GetSessionIdentityAsync(token).ConfigureAwait(false);
         using var api = PrintApiClient.ForSession(session);
         EnrollmentResponse response = await api.EnrollAsync(new EnrollmentRequest(request.EnrollmentCode ?? "", Environment.MachineName, PrintApiClient.AgentVersion), token).ConfigureAwait(false);
         using AgentStore store = new(directory, recoverUnreadableConfiguration: true);
         store.SaveConfiguration(new AgentConfiguration(request.ServerUrl, response.DeviceId, response.DeviceToken, response.PollIntervalSeconds));
         QqqSessionBinding expected = identity with { DeviceId = response.DeviceId };
         store.SaveAuthentication(new AgentAuthentication(expected, session.ExportCredential()));
         ResetConnectionState();
         QqqSessionBinding actual = await session.BindDeviceAsync(new QqqDeviceCredential(response.DeviceId, response.DeviceToken), cancellationToken: token).ConfigureAwait(false);
         RequireSameIdentity(expected, actual);
         ActivateSession(session, actual, store);
         lock(_stateLock)
         {
            _queuedJobs = [];
         }
         WriteLog("INFO", "Device enrolled with the QQQ user session.");
         WakePoll();
         return $"Enrolled device {response.DeviceId}";
      }
      finally { _authenticationGate.Release(); }
   }
}
