/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>One realtime message: a type, a message id for deduplication and an undecoded payload. The payload stays raw until the type is checked, so an unknown message cannot be misread as a known one.</summary>
public sealed record RealtimeEnvelope(string Type, string MessageId, JsonElement Payload)
{
   /// <summary>The size of the frame on the wire, filled in by the receiver for traffic accounting and never serialized, so it cannot be spoofed by a peer.</summary>
   [System.Text.Json.Serialization.JsonIgnore]
   public int WireLength { get; init; }
}
