/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The service's reply to a companion sign-out: the plain message, and the identity provider's sign-out request when the server
/// offers one, for the companion to open in the browser so the provider's own session ends too. It carries the provider's ID token
/// hint, so it is never logged and prints as fixed text.
/// </summary>
public sealed record PipeSignOutResult(string Message, QqqProviderLogout? ProviderLogout)
{
   /// <summary>Returns a fixed label instead of the generated record text, so logging a reply can never reveal the provider sign-out request.</summary>
   public override string ToString() => "Print agent sign-out result (provider request redacted)";
}
