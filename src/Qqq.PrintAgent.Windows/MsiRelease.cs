/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Validated metadata for the coherent x64 machine installer, before its bytes are downloaded.</summary>
public sealed record MsiRelease(string Version, Uri MsiUrl, Uri ChecksumUrl, long Size);
