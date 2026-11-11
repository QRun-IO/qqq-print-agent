/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// What a Start menu shortcut says: the program it launches, its arguments, its description, and, when the shortcut is
/// being written, its working folder and icon. A shortcut that was read carries only the first three, which are the ones
/// that decide whose shortcut it is.
/// </summary>
internal sealed record ShortcutLink(string Target, string Arguments, string Description, string WorkingDirectory = "", string IconLocation = "");
