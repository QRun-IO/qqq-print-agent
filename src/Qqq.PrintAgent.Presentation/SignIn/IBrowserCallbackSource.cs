/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Where browser callbacks arrive: the redirect address this application's installer registered, a way to open the inbox
/// for one exchange, and the protocol handler's recent trace, which shows whether the browser ever handed a callback over.
/// </summary>
public interface IBrowserCallbackSource
{
   /// <summary>The redirect address registered for this application; only a server that lists exactly this address is used.</summary>
   string RedirectUri { get; }



   /// <summary>The file the protocol handler writes its trace to, named in the log so an operator can find it.</summary>
   string TracePath { get; }



   /// <summary>Opens the inbox for one exchange.</summary>
   IBrowserCallbackInbox Open();



   /// <summary>The protocol handler's trace lines from the given recent window; empty when the browser never launched the handler.</summary>
   IReadOnlyList<string> RecentTrace(TimeSpan window);
}
