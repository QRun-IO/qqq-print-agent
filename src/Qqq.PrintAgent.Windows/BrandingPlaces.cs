/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Where branding works and what it reads: the installed companion, the all-users Start menu programs folder, the machine
/// state folder with the check that it is protected (it throws when the folder cannot be trusted), and the customer's
/// branding, loaded with or without the labels the installer recorded. The installed product passes the real places; a
/// test passes temporary folders and branding of its own.
/// </summary>
internal sealed record BrandingPlaces(string InstalledExecutable, string StartMenuPrograms, string StateDirectory, Action<string> EnsureProtectedState, Func<bool, RuntimeBranding> LoadBranding);