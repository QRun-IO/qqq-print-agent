/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// One command of a server's menu as the view shows it: which command it is, its label, the letter that chooses it
/// while the menu is open, the key that runs it from the tree without the menu (null when it has none), whether it
/// can be chosen now and, when it cannot, the sentence that says why and what to do.
/// </summary>
public sealed record ServerCommand(ServerCommandKind Kind, string Label, string AccessKey, string? Gesture, bool IsEnabled, string? DisabledReason);
