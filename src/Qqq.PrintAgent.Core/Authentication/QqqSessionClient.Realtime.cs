/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Realtime socket part of the session client. The socket is opened here, because only this class holds the session cookie and device
/// proof, and they are sent to the discovered origin and nowhere else. The platform socket itself comes from the transport; what
/// is sent on the upgrade and what each outcome means is decided here.
/// </summary>
public sealed partial class QqqSessionClient
{
   /// <summary>The subprotocol this agent speaks; a server that selects anything else is refused.</summary>
   private const string PrintingSubProtocol = "qqq-printing.v1";



   /// <summary>
   /// Refreshes standard discovery once per socket establishment, then connects only to that origin with the current
   /// session and approved device proof. The answer to the upgrade is read for the load balancer's cookies whatever the
   /// outcome, an accepted upgrade, a refusal with 401 or 403 and any other failure alike, because a refusal from a
   /// node that is stopping carries the load balancer's choice of node for the retry.
   /// </summary>
   public async Task<WebSocket> ConnectPrintingAsync(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, CancellationToken cancellationToken = default)
   {
      IQqqSocketConnector connector = _transport.CreateSocketConnector(TransportPolicy, ValidateSocketCertificate);
      try
      {
         (QqqSocketRequest request, long generation) = await PrepareSocketAsync(heartbeatInterval, heartbeatTimeout, cancellationToken).ConfigureAwait(false);
         _diagnostics.Info($"Opening the realtime socket to {QqqSessionDiagnostics.Describe(request.Uri)} with subprotocol {PrintingSubProtocol} (session cookie and device proof attached; heartbeat every {heartbeatInterval.TotalSeconds:0} s, timeout {heartbeatTimeout.TotalSeconds:0} s).");
         long started = _timeProvider.GetTimestamp();
         try
         { await connector.ConnectAsync(request, cancellationToken).ConfigureAwait(false); }
         finally { AdoptSocketAffinity(connector); }
         _diagnostics.Info($"Realtime socket upgraded in {Elapsed(started)}; server selected subprotocol {ShownOr(connector.SubProtocol, "(none)")}.");
         await ConfirmSocketAsync(connector.SubProtocol, generation, cancellationToken).ConfigureAwait(false);
         return connector.Release();
      }
      catch(WebSocketException exception) when(connector.UpgradeStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
      {
         _diagnostics.Info($"Realtime socket upgrade refused with HTTP {(int)connector.UpgradeStatus} ({exception.WebSocketErrorCode}); authorization is required.");
         connector.Dispose();
         throw new QqqSessionException(QqqSessionException.FailureKind.AuthenticationRequired, "QQQ authorization is required to restore the realtime connection.");
      }
      catch(Exception exception)
      {
         ReportSocketFailure(exception, connector.UpgradeStatus);
         connector.Dispose();
         throw;
      }
   }



   /// <summary>
   /// Under the client gate: requires a session, refreshes discovery, requires an approved device binding, and builds the
   /// upgrade request (address, subprotocol, cookies, origin, client channel, device proof and keep-alive). It
   /// returns the request with the session generation it was built under, so a session change during the connect is noticed.
   /// The upgrade carries the load balancer's cookies beside the session, so the socket opens on the node this client's
   /// HTTP requests already reach, and that node then serves its claims and downloads too. The discovery made just
   /// before may itself have rewritten those cookies, and the request is built after it, so it carries the newest.
   /// </summary>
   private async Task<(QqqSocketRequest Request, long Generation)> PrepareSocketAsync(TimeSpan heartbeatInterval, TimeSpan heartbeatTimeout, CancellationToken cancellationToken)
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
         var uri = new UriBuilder(new Uri(Origin, PrintingPath + "device/realtime")) { Scheme = string.Equals(Origin.Scheme, "https", StringComparison.Ordinal) ? "wss" : "ws" };
         KeyValuePair<string, string>[] headers =
         [
            new("Cookie", WithAffinity(CookieHeader())),
            new("Origin", Origin.GetLeftPart(UriPartial.Authority)),
            new("X-QQQ-Client", ClientChannel),
            new("X-QQQ-Device-Id", _device.DeviceId),
            new("X-QQQ-Device-Token", _device.DeviceToken),
         ];
         return (new QqqSocketRequest(uri.Uri, PrintingSubProtocol, headers, heartbeatInterval, heartbeatTimeout), _generation);
      }
      finally { _gate.Release(); }
   }



   /// <summary>Under the client gate: refuses the upgraded socket when the session changed while it was connecting, or when the server did not select this agent's subprotocol.</summary>
   private async Task ConfirmSocketAsync(string? subProtocol, long generation, CancellationToken cancellationToken)
   {
      await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
      try
      {
         if(generation != _generation)
         {
            _diagnostics.Info("Realtime socket discarded: the session changed while it was connecting.");
            throw AuthenticationRequired();
         }
         if(!string.Equals(subProtocol, PrintingSubProtocol, StringComparison.Ordinal))
         {
            _diagnostics.Info("Realtime socket refused: the server did not select the qqq-printing.v1 subprotocol.");
            throw InvalidResponse();
         }
      }
      finally { _gate.Release(); }
   }



   /// <summary>
   /// The certificate decision for the HTTP client: the same logged policy as everywhere else, judged for the address the
   /// request is going to, which is what decides whether the certificate exception applies, or for the origin when a
   /// request carries no address.
   /// </summary>
   private bool ValidateHttpCertificate(HttpRequestMessage request, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
      => EvaluateCertificate(request.RequestUri ?? Origin, certificate, errors);



   /// <summary>
   /// The certificate decision for the WebSocket upgrade: the same logged policy, so a rejected or explicitly allowed
   /// certificate is reported the same way for the socket as for HTTP. The upgrade only ever goes to this client's own
   /// origin and follows no redirect, so that origin is the address the certificate exception is asked about. The
   /// platform may hand over the older certificate type, which is converted so its details can be logged.
   /// </summary>
   private bool ValidateSocketCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
      => EvaluateCertificate(Origin, certificate is null ? null : certificate as X509Certificate2 ?? new X509Certificate2(certificate), errors);



   /// <summary>Logs why the socket could not be established: the WebSocket error and any HTTP status from the upgrade, a cancellation, or the exception type and message.</summary>
   private void ReportSocketFailure(Exception exception, HttpStatusCode upgradeStatus)
   {
      int status = (int)upgradeStatus;
      _diagnostics.Info(exception is WebSocketException webSocket
         ? $"Realtime socket upgrade failed: {webSocket.WebSocketErrorCode}{(status == 0 ? "" : ", HTTP " + status.ToString(System.Globalization.CultureInfo.InvariantCulture))}: {ShownText(webSocket.Message)}"
         : exception is OperationCanceledException ? "Realtime socket connection was cancelled or timed out."
         : "Realtime socket connection failed: " + ShownText(QqqSessionDiagnostics.Describe(exception)));
   }



   /// <summary>Refreshes discovery under the caller-held session gate and rejects incompatible printing majors before sending any WebSocket credentials.</summary>
   private async Task RefreshPrintingDiscoveryAsync(CancellationToken token)
   {
      _discovery = null;
      QqqApiDiscovery discovery = await DiscoverCoreAsync(token).ConfigureAwait(false);
      // Discovery already required exactly one printing extension with a plain 1.x or 2.x version, so only the major is left to check.
      string version = discovery.PrintingExtensionVersion ?? "";
      if(!version.StartsWith("2.", StringComparison.Ordinal))
      {
         _diagnostics.Info($"Realtime printing needs printing extension 2.x; the server advertises {version}.");
         throw new QqqSessionException(QqqSessionException.FailureKind.ConfigurationRequired, "Upgrade the server to printing extension 2.x for realtime printing.");
      }
      _diagnostics.Info($"Printing extension {version} supports realtime; media negotiation {(discovery.SupportsPrinterMedia() ? "available" : "not available (needs 2.3+)")}.");
   }
}
