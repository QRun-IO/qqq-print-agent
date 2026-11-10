/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// How the platform transport must behave, chosen by the session client: whether redirects are followed and whether the
/// platform keeps its own cookie jar. The client sets both to false, because a redirect could carry credentials to
/// another host and the session cookie is attached by hand to exactly the requests that may carry it.
/// </summary>
internal sealed record QqqTransportPolicy(bool FollowRedirects, bool UseCookieJar);
