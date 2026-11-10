/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Session part of the API client: the signed-in QQQ session every request goes through, which is the only thing that authenticates
/// one and which confines the requests to the printing extension's path.
/// </summary>
public sealed partial class PrintApiClient
{
   private readonly IQqqSessionClient _session;



   /// <summary>Builds the client for a signed-in session, the only way to build one. The session remains owned by the service; disposing an API request adapter does not sign it out. The clock is the one the client counts its limits on, the system clock by default.</summary>
   public static PrintApiClient ForSession(IQqqSessionClient session, TimeProvider? timeProvider = null) => new(session, timeProvider);
}
