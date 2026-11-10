/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>One realtime message: a type, a message id for deduplication and an undecoded payload. The payload stays raw until the type is checked, so an unknown message cannot be misread as a known one.</summary>
public sealed record RealtimeEnvelope(string Type, string MessageId, JsonElement Payload)
{
   private const int TypeLimit = 64;
   private const int MessageIdLimit = 200;
   private const int DepthLimit = 24;



   /// <summary>The size of the frame on the wire, filled in by the receiver for traffic accounting and never serialized, so it cannot be spoofed by a peer.</summary>
   [System.Text.Json.Serialization.JsonIgnore]
   public int WireLength { get; init; }



   /// <summary>
   /// Reads one complete frame from the server. It must be a JSON object within the depth limit, with no repeated field,
   /// a short type, a message identifier of at most 200 characters and an object payload; anything else is a protocol
   /// violation. The payload is copied out of the parse buffer, so the envelope stays valid after the reader moves on.
   /// </summary>
   internal static RealtimeEnvelope Parse(ReadOnlyMemory<byte> frame)
   {
      using JsonDocument document = ProtocolJson.ParseObject(frame, "realtime frame", DepthLimit);
      ProtocolJson.RejectRepeatedFields(document.RootElement, "realtime frame");
      RealtimeEnvelope envelope = ProtocolJson.Read<RealtimeEnvelope>(document.RootElement, "realtime frame");
      if(string.IsNullOrEmpty(envelope.Type) || envelope.Type.Length > TypeLimit || string.IsNullOrEmpty(envelope.MessageId) || envelope.MessageId.Length > MessageIdLimit
         || envelope.Payload.ValueKind != JsonValueKind.Object)
      {
         throw new PrintingProtocolException("The realtime frame has no usable type, message identifier or payload object.");
      }
      return envelope with { Payload = envelope.Payload.Clone(), WireLength = frame.Length };
   }



   /// <summary>
   /// Converts the payload into the contract type a message of this kind carries. A member of the wrong JSON type is
   /// reported as a protocol violation, the same failure a malformed frame raises, so a consumer of realtime messages has
   /// one exception type to handle for everything the server can get wrong.
   /// </summary>
   public T ReadPayload<T>() => Payload.ValueKind == JsonValueKind.Object
      ? ProtocolJson.Read<T>(Payload, "realtime payload")
      : throw new PrintingProtocolException("The realtime payload is not a JSON object.");
}
