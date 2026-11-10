/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>One open browser callback pipe, as the sign-in flow uses it: wait for the forwarded callback, then close.</summary>
internal sealed class BrowserCallbackInbox(QqqBrowserCallback inbox) : IBrowserCallbackInbox
{
   /// <summary>Waits for the callback the protocol handler forwards.</summary>
   public Task<Uri> ReceiveAsync(CancellationToken cancellationToken) => inbox.ReceiveAsync(cancellationToken);



   /// <summary>Closes the pipe.</summary>
   public void Dispose() => inbox.Dispose();
}
