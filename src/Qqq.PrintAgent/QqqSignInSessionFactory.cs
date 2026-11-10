/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Presentation.SignIn;

namespace Qqq.PrintAgent;

/// <summary>
/// Creates the real session client for a sign-in attempt. Plain HTTP is allowed only for this computer itself, the
/// certificate exception is passed through for the one server it was saved for, and the client's progress goes to the
/// shared activity log: steps at INFO, request detail at DEBUG.
/// </summary>
internal sealed class QqqSignInSessionFactory : ISignInSessionFactory
{
   /// <summary>Creates a session for the server; an address the client refuses is thrown as an argument exception for the flow to report.</summary>
   public ISignInSession Create(Uri origin, bool allowUntrustedCertificate)
      => new QqqSignInSession(new QqqSessionClient(origin, allowLoopbackHttp: origin.IsLoopback, diagnostics: Diagnostics(), allowUntrustedCertificate: allowUntrustedCertificate));



   /// <summary>The companion's progress sink for the session client, in the shared log the service also writes.</summary>
   private static QqqSessionDiagnostics Diagnostics() => new(message => App.Log.Info("[Companion] " + message),
      message => App.Log.Debug("[Companion] " + message), () => App.Log.IsDebugEnabled);
}
