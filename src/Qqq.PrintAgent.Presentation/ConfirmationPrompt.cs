/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>The wording of one "are you sure" dialog: its title, what the action will and will not do, and the label of the button that goes ahead. The view always makes Back the default button.</summary>
public sealed record ConfirmationPrompt(string Title, string Message, string Action);
