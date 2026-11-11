/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// What a bitmap handed to GDI looks like: its width and height in pixels (a negative height means the first row is the
/// top one), the number of planes and the bits per pixel. These are the fields of BITMAPINFOHEADER the printing code
/// chooses; the rest of that structure (its own size, no compression) is filled in where the native call is made.
/// </summary>
internal sealed record GdiBitmapHeader(int Width, int Height, ushort Planes, ushort BitCount);
