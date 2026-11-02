/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Testing;

/// <summary>A real loopback RFC6455 handshake; no installed service, URL reservation, or printer is involved.</summary>
internal sealed class LoopbackWebSocketPeer : IDisposable
{
   private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
   private readonly List<TcpClient> _clients = [];
   internal Uri Origin { get; }
   internal Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>Starts listening on an ephemeral loopback port, so each test gets its own address and nothing else on the machine can connect.</summary>
   internal LoopbackWebSocketPeer()
   {
      _listener.Start();
      Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
   }



   /// <summary>
   /// Accepts one connection, requires the realtime path, records the upgrade request's headers (so tests can check which
   /// credentials were presented), answers the RFC 6455 handshake and returns the server end of the socket.
   /// </summary>
   internal async Task<WebSocket> AcceptAsync(CancellationToken token)
   {
      TcpClient client = await _listener.AcceptTcpClientAsync(token);
      _clients.Add(client);
      NetworkStream stream = client.GetStream();
      using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
      string first = await reader.ReadLineAsync(token) ?? throw new IOException("Missing handshake.");
      if(!first.StartsWith("GET /qqq/v1/qbit/printing/device/realtime ", StringComparison.Ordinal))
      { throw new IOException("Unexpected websocket path."); }
      Headers.Clear();
      while(await reader.ReadLineAsync(token) is { Length: > 0 } line)
      {
         string[] pair = line.Split(':', 2);
         Headers[pair[0]] = pair[1].Trim();
      }
      // RFC6455 requires SHA-1 for the public handshake challenge; this is not an authentication digest.
#pragma warning disable CA5350
      string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(Headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
#pragma warning restore CA5350
      byte[] response = Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\nSec-WebSocket-Protocol: qqq-printing.v1\r\n\r\n");
      await stream.WriteAsync(response, token);
      return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, SubProtocol = "qqq-printing.v1" });
   }



   /// <summary>Sends one realtime message in the protocol envelope (type, a fresh message id and the payload) as a single text frame.</summary>
   internal static Task SendAsync(WebSocket socket, string type, object payload, CancellationToken token)
      => socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type, messageId = Guid.NewGuid().ToString("N"), payload }, PrintApiClient.JsonOptions), WebSocketMessageType.Text, true, token);



   /// <summary>Reads one complete (possibly fragmented) message of up to 1 MiB and parses it as an envelope; a close frame is reported as an error.</summary>
   internal static async Task<RealtimeEnvelope> ReceiveAsync(WebSocket socket, CancellationToken token)
   {
      byte[] bytes = new byte[1048576];
      int offset = 0;
      WebSocketReceiveResult result;
      do
      {
         result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes, offset, bytes.Length - offset), token);
         offset += result.Count;
         if(result.MessageType == WebSocketMessageType.Close)
         { throw new IOException("Socket closed."); }
      } while(!result.EndOfMessage);
      return JsonSerializer.Deserialize<RealtimeEnvelope>(bytes.AsSpan(0, offset), PrintApiClient.JsonOptions) ?? throw new IOException("Missing message.");
   }



   /// <summary>Stops listening and closes every accepted connection.</summary>
   public void Dispose()
   {
      _listener.Stop();
      foreach(TcpClient client in _clients)
      { client.Dispose(); }
   }
}
