/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

public sealed record RealtimeEnvelope(string Type, string MessageId, JsonElement Payload)
{
   [System.Text.Json.Serialization.JsonIgnore]
   public int WireLength { get; init; }
}
