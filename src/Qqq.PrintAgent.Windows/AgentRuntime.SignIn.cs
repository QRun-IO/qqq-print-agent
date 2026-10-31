using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   internal async Task<string> SignInAsync(PipeRequest request, CancellationToken token)
      => await ConnectionTransitionAsync(() => SignInCoreAsync(request, token), token).ConfigureAwait(false);

   private async Task<string> SignInCoreAsync(PipeRequest request, CancellationToken token)
   {
      QqqSessionCredential credential = request.SessionCredential ?? throw new InvalidOperationException("A QQQ sign-in session is required.");
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      IQqqSessionClient? candidate = null;
      try
      {
         candidate = NewSession(credential.Origin);
         await candidate.RestoreSessionAsync(credential, token).ConfigureAwait(false);
         using AgentStore store = new(directory);
         AgentConfiguration? configuration = store.LoadConfiguration();
         QqqSessionBinding binding = await candidate.GetSessionIdentityAsync(token).ConfigureAwait(false);
         AgentAuthentication? saved = store.LoadAuthentication();
         if(configuration is not null && SameOrigin(credential.Origin, configuration.ServerUrl))
         {
            binding = await candidate.BindDeviceAsync(new QqqDeviceCredential(configuration.DeviceId, configuration.DeviceToken), request.ClaimLegacyDevice, token).ConfigureAwait(false);
            if(saved?.Binding.DeviceId is not null && SameOrigin(saved.Binding.Origin, configuration.ServerUrl))
            {
               RequireSameIdentity(saved.Binding, binding);
            }
            store.SaveControlState(store.LoadControlState() with { AuthorizationRequired = false });
         }
         IQqqSessionClient? previous = _sessionClient;
         ResetConnectionState();
         ActivateSession(candidate, binding, store);
         previous?.Dispose();
         WakePoll();
         return "Signed in to QQQ. " + _authenticationMessage;
      }
      catch
      {
         candidate?.Dispose();
         throw;
      }
      finally { _authenticationGate.Release(); }
   }

   internal async Task<string> SignOutAsync(CancellationToken token)
      => await ConnectionTransitionAsync(() => SignOutCoreAsync(token), token).ConfigureAwait(false);

   private async Task<string> SignOutCoreAsync(CancellationToken token)
   {
      await _authenticationGate.WaitAsync(token).ConfigureAwait(false);
      try
      {
         BlockAuthorization();
         using AgentStore store = new(directory);
         AgentAuthentication? saved = store.LoadAuthentication();
         if(saved is not null)
         {
            store.SaveAuthentication(new AgentAuthentication(saved.Binding, null));
         }
         try
         {
            if(_sessionClient is null && saved?.Credential is not null)
            {
               _sessionClient = NewSession(saved.Credential.Origin);
               await _sessionClient.RestoreSessionAsync(saved.Credential, token).ConfigureAwait(false);
            }
            if(_sessionClient is not null)
            {
               await _sessionClient.SignOutAsync(token).ConfigureAwait(false);
            }
         }
         finally
         {
            _sessionClient?.Dispose();
            _sessionClient = null;
            ResetConnectionState();
         }
         return "Signed out. Device pairing and print history were preserved.";
      }
      finally { _authenticationGate.Release(); }
   }
}
