/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// One rendered page: its pixels as 32-bit BGRA with the first row at the top, and the width and height in pixels the
/// renderer reported for them.
/// </summary>
internal sealed record PageBitmap(byte[] Pixels, uint PixelWidth, uint PixelHeight);
