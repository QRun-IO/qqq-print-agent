/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>One message for the window's message bar: a short title, the explanation and how serious it is. Presentation logic returns these and the view shows them, so the wording of every result can be tested.</summary>
public sealed record FeedbackMessage(string Title, string Message, FeedbackSeverity Severity);
