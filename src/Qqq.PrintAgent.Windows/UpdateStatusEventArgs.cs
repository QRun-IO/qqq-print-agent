/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

public sealed class UpdateStatusEventArgs(string message) : EventArgs
{
   public string Message { get; } = message;
}
