/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;


namespace Qqq.PrintAgent.Core;

/// <summary>Source-generated JSON serializer for <see cref="AppSettings"/>, so trimmed builds need no reflection.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsContext : JsonSerializerContext;
