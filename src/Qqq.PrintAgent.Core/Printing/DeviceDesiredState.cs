/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Core.Printing;

public sealed record DeviceDesiredState([property: JsonRequired] long Revision, [property: JsonRequired] bool DispatchPaused, [property: JsonRequired] string ConnectionState);
