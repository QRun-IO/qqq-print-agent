/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.WebSockets;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// One attempt to open the realtime socket. The session client supplies the request, reads the outcome and decides what
/// it means; the connector only performs the upgrade. Disposing it abandons the attempt, and
/// <see cref="Release"/> hands the connected socket to the caller instead.
/// </summary>
internal interface IQqqSocketConnector : IDisposable
{
   /// <summary>The HTTP status the server answered the upgrade with, or zero when there was no HTTP answer; it tells a refused authorization from a broken connection.</summary>
   HttpStatusCode UpgradeStatus { get; }



   /// <summary>The subprotocol the server selected, or null when it selected none.</summary>
   string? SubProtocol { get; }



   /// <summary>Performs the upgrade described by the request; it throws when the connection or the upgrade fails.</summary>
   Task ConnectAsync(QqqSocketRequest request, CancellationToken cancellationToken);



   /// <summary>Returns the connected socket, which from then on owns the transport underneath it. The connector must not be disposed afterwards.</summary>
   WebSocket Release();
}
