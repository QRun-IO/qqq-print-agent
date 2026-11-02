/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Carries one update-status message to subscribers, so the UI can show update progress without reaching into the update logic.</summary>
public sealed class UpdateStatusEventArgs(string message) : EventArgs
{
   public string Message { get; } = message;
}
