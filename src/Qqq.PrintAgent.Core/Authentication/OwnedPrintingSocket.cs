/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net.WebSockets;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>Keeps the redirect-refusing HTTP transport alive until the upgraded socket is disposed.</summary>
internal sealed class OwnedPrintingSocket(WebSocket socket, HttpMessageInvoker invoker) : WebSocket
{
   /// <summary>Why the wrapped socket closed, passed straight through so callers see the real close status.</summary>
   public override WebSocketCloseStatus? CloseStatus => socket.CloseStatus;



   /// <summary>The wrapped socket's close description.</summary>
   public override string? CloseStatusDescription => socket.CloseStatusDescription;



   /// <summary>The wrapped socket's state.</summary>
   public override WebSocketState State => socket.State;



   /// <summary>The negotiated subprotocol of the wrapped socket.</summary>
   public override string? SubProtocol => socket.SubProtocol;



   /// <summary>Drops the connection immediately without a close handshake.</summary>
   public override void Abort() => socket.Abort();



   /// <summary>Closes the wrapped socket with the full close handshake.</summary>
   public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
      => socket.CloseAsync(closeStatus, statusDescription, cancellationToken);



   /// <summary>Closes only the sending side of the wrapped socket.</summary>
   public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
      => socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);



   /// <summary>Receives the next message fragment from the wrapped socket.</summary>
   public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
      => socket.ReceiveAsync(buffer, cancellationToken);



   /// <summary>Sends a message fragment on the wrapped socket.</summary>
   public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
      => socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);



   /// <summary>Disposes the socket and then the HTTP transport that carried its upgrade, which must stay alive until now because disposing it earlier would end the connection.</summary>
   public override void Dispose()
   {
      socket.Dispose();
      invoker.Dispose();
   }
}
