/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Where branding works and what it reads: the installed companion, the all-users Start menu programs folder, the machine
/// state folder with the check that it is protected (it throws when the folder cannot be trusted), and the customer's
/// branding, loaded with or without the labels the installer recorded, and where a note goes when branding went ahead
/// although it found something it left alone. The installed product passes the real places and the machine activity log;
/// a test passes temporary folders, branding of its own and a list.
/// </summary>
internal sealed record BrandingPlaces(string InstalledExecutable, string StartMenuPrograms, string StateDirectory, Action<string> EnsureProtectedState, Func<bool, RuntimeBranding> LoadBranding, Action<string> Note);
