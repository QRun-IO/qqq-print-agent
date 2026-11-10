/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Native session part of the API client: routes every request through the signed-in QQQ session, the only thing that authenticates
/// one, and confines those requests to the printing extension's path.
/// </summary>
public sealed partial class PrintApiClient
{
   private bool _nativeSession;
   private IQqqSessionClient? _nativeSessionClient;



   /// <summary>The session remains owned by the service; disposing an API request adapter does not sign it out. The clock is the one the client counts its limits on, the system clock by default.</summary>
   public static PrintApiClient ForSession(IQqqSessionClient session, TimeProvider? timeProvider = null)
   {
      PrintApiClient client = new(session.Origin.AbsoluteUri, new PrintingSessionHandler(session), timeProvider);
      client._nativeSession = true;
      client._nativeSessionClient = session;
      client._http.BaseAddress = new Uri(session.Origin, PrintingSessionHandler.Prefix);
      return client;
   }
}
