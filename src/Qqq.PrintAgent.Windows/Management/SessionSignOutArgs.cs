/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// Arguments of <see cref="ManagementOperations.SessionSignOut"/>. When the caller gives the application's registered
/// callback address, the service also asks the server for the identity provider's sign-out request and returns it; the
/// companion does, because it can open a browser. The command line leaves it out and only the QQQ session ends. Every
/// member is optional, so a request without arguments is valid.
/// </summary>
public sealed record SessionSignOutArgs(string? PostLogoutRedirectUri = null);
