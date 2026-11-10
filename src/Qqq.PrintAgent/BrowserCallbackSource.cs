/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>The sign-in flow's view of the installed browser callback: the registered redirect address, the pipe inbox the protocol handler forwards to, and that handler's trace file.</summary>
internal sealed class BrowserCallbackSource : IBrowserCallbackSource
{
   /// <summary>The redirect address the installer registered for this application.</summary>
   public string RedirectUri => QqqBrowserCallback.RedirectUri;



   /// <summary>The file the protocol handler writes its trace to.</summary>
   public string TracePath => QqqBrowserCallback.TracePath;



   /// <summary>Opens the pipe inbox for one exchange.</summary>
   public IBrowserCallbackInbox Open() => new BrowserCallbackInbox(new QqqBrowserCallback());



   /// <summary>The protocol handler's recent trace lines.</summary>
   public IReadOnlyList<string> RecentTrace(TimeSpan window) => QqqBrowserCallback.RecentTrace(window);
}
