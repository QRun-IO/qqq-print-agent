using Qqq.PrintAgent.Core.Transport;

namespace Qqq.PrintAgent.Windows;

public sealed partial class AgentRuntime
{
   private readonly ApplicationTrafficMeter _traffic = new(timeProvider);
   private Uri? _transportOrigin;
   private bool _transportConnectedBefore;
   private bool _transportNeedsSignIn;
   private bool _transportStopped;

   /// <summary>Memory-only observation: no printer discovery, journal access or cloud request.</summary>
   public AgentTransportSnapshot GetTransportSnapshot()
   {
      lock(_stateLock)
      {
         ApplicationTrafficSnapshot bytes = _traffic.GetSnapshot();
         string protocol = _transportOrigin is null ? "NONE" : apiFactory is not null ? "HTTP" : _transportOrigin.Scheme is "https" ? "WSS" : "WS";
         string state = _transportStopped || _transportOrigin is null ? "DISCONNECTED"
            : _authorizationBlocked || _transportNeedsSignIn ? "AUTHORIZATION_REQUIRED"
            : !_authorizationReady ? "DISCONNECTED"
            : _realtimeReady && _realtime?.IsOpen == true ? "CONNECTED"
            : _transportConnectedBefore || _reconnectAttempts > 0 ? "RECONNECTING" : "CONNECTING";
         return new AgentTransportSnapshot(protocol, state, bytes.UploadedBytes, bytes.DownloadedBytes,
            bytes.UploadBytesPerSecond, bytes.DownloadBytesPerSecond, bytes.TotalsSinceUtc);
      }
   }
}
