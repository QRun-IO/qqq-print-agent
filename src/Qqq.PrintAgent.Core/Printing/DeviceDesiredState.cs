/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>What the operator wants for this device (dispatch paused or not, connection state), versioned by revision. Every field is required so a reply missing one is rejected instead of silently resuming dispatch.</summary>
public sealed record DeviceDesiredState([property: JsonRequired] long Revision, [property: JsonRequired] bool DispatchPaused, [property: JsonRequired] string ConnectionState);
