/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>An open inbox for the one callback the browser sends back to this application. It is opened before the browser is, so a fast redirect is not lost, and disposed when the exchange ends.</summary>
public interface IBrowserCallbackInbox : IDisposable
{
   /// <summary>Waits for the callback address, until the token is cancelled.</summary>
   Task<Uri> ReceiveAsync(CancellationToken cancellationToken);
}
