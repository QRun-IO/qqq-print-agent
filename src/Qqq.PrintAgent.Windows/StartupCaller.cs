/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Who is asking for a change to sign-in startup: the user's security identifier (null for an identity that has none),
/// whether the process holds administrator rights, and the path of the program that is running.
/// </summary>
internal sealed record StartupCaller(string? UserSid, bool IsAdministrator, string? ProcessPath);
