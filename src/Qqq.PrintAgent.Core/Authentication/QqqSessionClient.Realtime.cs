/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Realtime socket part of the session client. The socket is opened here, because only this class holds the session cookie and device
/// proof, and they are sent to the discovered origin and nowhere else.
/// </summary>
public sealed partial class QqqSessionClient
{
   /// <summary>Refreshes standard discovery once per socket establishment, then connects only to that origin with the current session and approved device proof.</summary>
   public async Task<WebSocket> ConnectPrintingAsync(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, CancellationToken cancellationToken = default)
   {
      HttpMessageInvoker invoker = CreateSocketInvoker();
      var socket = new ClientWebSocket();
      long generation;
      try
      {
         await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
         try
         {
            RequireAuthenticated();
            _diagnostics.Info("Preparing the realtime printing socket: refreshing discovery first.");
            await RefreshPrintingDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            if(_device is null || _binding?.HasApprovedDevice != true)
            {
               _diagnostics.Info("Realtime socket refused locally: no approved device binding is held. Enroll or re-associate this computer first.");
               throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "An approved device binding is required.");
            }
            generation = _generation;
            ConfigureSocket(socket, _device, heartbeatInterval, heartbeatTimeout);
         }
         finally { _gate.Release(); }
         var uri = new UriBuilder(new Uri(Origin, PrintingPath + "device/realtime")) { Scheme = string.Equals(Origin.Scheme, "https", StringComparison.Ordinal) ? "wss" : "ws" };
         _diagnostics.Info($"Opening the realtime socket to {QqqSessionDiagnostics.Describe(uri.Uri)} with subprotocol qqq-printing.v1 (session cookie and device proof attached; heartbeat every {heartbeatInterval.TotalSeconds:0} s, timeout {heartbeatTimeout.TotalSeconds:0} s).");
         long started = System.Diagnostics.Stopwatch.GetTimestamp();
         try
         { await socket.ConnectAsync(uri.Uri, invoker, cancellationToken).ConfigureAwait(false); }
         finally { AdoptSocketAffinity(socket); }
         _diagnostics.Info($"Realtime socket upgraded in {Elapsed(started)}; server selected subprotocol {socket.SubProtocol ?? "(none)"}.");
         await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
         try
         {
            if(generation != _generation)
            {
               _diagnostics.Info("Realtime socket discarded: the session changed while it was connecting.");
               throw AuthenticationRequired();
            }
            if(!string.Equals(socket.SubProtocol, "qqq-printing.v1", StringComparison.Ordinal))
            {
               _diagnostics.Info("Realtime socket refused: the server did not select the qqq-printing.v1 subprotocol.");
               throw InvalidResponse();
            }
         }
         finally { _gate.Release(); }
         return new OwnedPrintingSocket(socket, invoker);
      }
      catch(WebSocketException exception) when(socket.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
      {
         _diagnostics.Info($"Realtime socket upgrade refused with HTTP {(int)socket.HttpStatusCode} ({exception.WebSocketErrorCode}); authorization is required.");
         socket.Dispose();
         invoker.Dispose();
         throw new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "QQQ authorization is required to restore the realtime connection.");
      }
      catch(Exception exception)
      {
         ReportSocketFailure(exception, socket);
         socket.Dispose();
         invoker.Dispose();
         throw;
      }
   }



   /// <summary>
   /// The HTTP invoker for the WebSocket upgrade: no redirects, no cookie jar (the cookies are attached by hand) and the same
   /// certificate policy as the HTTP client, so a rejected or explicitly allowed certificate is logged the same way. See the
   /// MA0039 note on the constructor.
   /// </summary>
   private HttpMessageInvoker CreateSocketInvoker()
   {
#pragma warning disable MA0039
      return new HttpMessageInvoker(new SocketsHttpHandler
      {
         AllowAutoRedirect = false,
         UseCookies = false,
         SslOptions = new System.Net.Security.SslClientAuthenticationOptions
         {
            RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => EvaluateCertificate(Origin.Authority,
               certificate is null ? null : certificate as System.Security.Cryptography.X509Certificates.X509Certificate2 ?? new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate), errors),
         },
      });
#pragma warning restore MA0039
   }



   /// <summary>
   /// Sets the subprotocol, cookies, origin, client channel, device proof and keep-alive on a socket before it connects; the
   /// caller holds the gate. The upgrade carries the load balancer's cookies beside the session, so the socket opens on the
   /// node this client's HTTP requests already reach, and that node then serves its claims and downloads too.
   /// </summary>
   private void ConfigureSocket(ClientWebSocket socket, QqqDeviceCredential device, TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout)
   {
      socket.Options.AddSubProtocol("qqq-printing.v1");
      socket.Options.CollectHttpResponseDetails = true;
      socket.Options.SetRequestHeader("Cookie", WithAffinity(CookieHeader()));
      socket.Options.SetRequestHeader("Origin", Origin.GetLeftPart(UriPartial.Authority));
      socket.Options.SetRequestHeader("X-QQQ-Client", ClientChannel);
      socket.Options.SetRequestHeader("X-QQQ-Device-Id", device.DeviceId);
      socket.Options.SetRequestHeader("X-QQQ-Device-Token", device.DeviceToken);
      socket.Options.KeepAliveInterval = heartbeatInterval;
      socket.Options.KeepAliveTimeout = heartbeatTimeout;
   }



   /// <summary>Logs why the socket could not be established: the WebSocket error and any HTTP status from the upgrade, a cancellation, or the exception type and message.</summary>
   private void ReportSocketFailure(Exception exception, ClientWebSocket socket)
   {
      int status = (int)socket.HttpStatusCode;
      _diagnostics.Info(exception is WebSocketException webSocket
         ? $"Realtime socket upgrade failed: {webSocket.WebSocketErrorCode}{(status == 0 ? "" : ", HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture))}: {webSocket.Message}"
         : exception is OperationCanceledException ? "Realtime socket connection was cancelled or timed out."
         : "Realtime socket connection failed: " + QqqSessionDiagnostics.Describe(exception));
   }



   /// <summary>Refreshes discovery under the caller-held session gate and rejects incompatible printing majors before sending any WebSocket credentials.</summary>
   private async Task RefreshPrintingDiscoveryAsync(CancellationToken token)
   {
      _discovery = null;
      QqqApiDiscovery discovery = await DiscoverCoreAsync(token).ConfigureAwait(false);
      QqqApiDiscovery.Extension extension = discovery.Extensions.Single(item => string.Equals(item.Name, "printing", StringComparison.Ordinal));
      if(!Version.TryParse(extension.Version, out Version? version) || version.Major != 2)
      {
         _diagnostics.Info($"Realtime printing needs printing extension 2.x; the server advertises {extension.Version}.");
         throw new QqqSessionException(QqqSessionException.FailureKind.ConfigurationRequired, "Upgrade the server to printing extension 2.x for realtime printing.");
      }
      _diagnostics.Info($"Printing extension {extension.Version} supports realtime; media negotiation {(discovery.SupportsPrinterMedia() ? "available" : "not available (needs 2.3+)")}.");
   }
}
