using System.Net.WebSockets;

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>Keeps the redirect-refusing HTTP transport alive until the upgraded socket is disposed.</summary>
internal sealed class OwnedPrintingSocket(WebSocket socket, HttpMessageInvoker invoker) : WebSocket
{
   public override WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
   public override string? CloseStatusDescription => socket.CloseStatusDescription;
   public override WebSocketState State => socket.State;
   public override string? SubProtocol => socket.SubProtocol;
   public override void Abort() => socket.Abort();
   public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
      => socket.CloseAsync(closeStatus, statusDescription, cancellationToken);
   public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
      => socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
   public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
      => socket.ReceiveAsync(buffer, cancellationToken);
   public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
      => socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
   public override void Dispose()
   {
      socket.Dispose();
      invoker.Dispose();
   }
}
