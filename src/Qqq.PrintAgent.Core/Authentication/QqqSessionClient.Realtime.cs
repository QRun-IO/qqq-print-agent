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
      var invoker = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
      var socket = new ClientWebSocket();
      long generation;
      try
      {
         await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
         try
         {
            RequireAuthenticated();
            await RefreshPrintingDiscoveryAsync(cancellationToken).ConfigureAwait(false);
            if(_device is null || _binding?.HasApprovedDevice != true)
            {
               throw new QqqSessionException(QqqSessionException.FailureKind.PermissionDenied, "An approved device binding is required.");
            }
            generation = _generation;
            socket.Options.AddSubProtocol("qqq-printing.v1");
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.SetRequestHeader("Cookie", CookieHeader());
            socket.Options.SetRequestHeader("Origin", Origin.GetLeftPart(UriPartial.Authority));
            socket.Options.SetRequestHeader("X-QQQ-Client", ClientChannel);
            socket.Options.SetRequestHeader("X-QQQ-Device-Id", _device.DeviceId);
            socket.Options.SetRequestHeader("X-QQQ-Device-Token", _device.DeviceToken);
            socket.Options.KeepAliveInterval = heartbeatInterval;
            socket.Options.KeepAliveTimeout = heartbeatTimeout;
         }
         finally { _gate.Release(); }
         var uri = new UriBuilder(new Uri(Origin, PrintingPath + "device/realtime")) { Scheme = string.Equals(Origin.Scheme, "https", StringComparison.Ordinal) ? "wss" : "ws" };
         await socket.ConnectAsync(uri.Uri, invoker, cancellationToken).ConfigureAwait(false);
         await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
         try
         {
            if(generation != _generation)
            { throw AuthenticationRequired(); }
            if(!string.Equals(socket.SubProtocol, "qqq-printing.v1", StringComparison.Ordinal))
            {
               throw InvalidResponse();
            }
         }
         finally { _gate.Release(); }
         return new OwnedPrintingSocket(socket, invoker);
      }
      catch(WebSocketException) when(socket.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
      {
         socket.Dispose();
         invoker.Dispose();
         throw new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "QQQ authorization is required to restore the realtime connection.");
      }
      catch
      {
         socket.Dispose();
         invoker.Dispose();
         throw;
      }
   }



   /// <summary>Refreshes discovery under the caller-held session gate and rejects incompatible printing majors before sending any WebSocket credentials.</summary>
   private async Task RefreshPrintingDiscoveryAsync(CancellationToken token)
   {
      _discovery = null;
      QqqApiDiscovery discovery = await DiscoverCoreAsync(token).ConfigureAwait(false);
      QqqApiDiscovery.Extension extension = discovery.Extensions.Single(item => string.Equals(item.Name, "printing", StringComparison.Ordinal));
      if(!Version.TryParse(extension.Version, out Version? version) || version.Major != 2)
      {
         throw new QqqSessionException(QqqSessionException.FailureKind.ConfigurationRequired, "Upgrade the server to printing extension 2.x for realtime printing.");
      }
   }
}
