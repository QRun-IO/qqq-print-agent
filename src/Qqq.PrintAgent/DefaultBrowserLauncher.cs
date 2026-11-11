/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using Qqq.PrintAgent.Presentation.SignIn;

namespace Qqq.PrintAgent;

/// <summary>Opens an address in the user's default browser through the Windows shell. The address can carry a sign-in state or a token hint, so it is handed straight to the shell and never logged here.</summary>
internal sealed class DefaultBrowserLauncher : IBrowserLauncher
{
   /// <summary>Starts the default browser on the address; a failure to start it is thrown to the presentation assembly. Windows' text for that failure names the whole address, so nothing here logs it, and the presentation assembly reports only the kind of failure.</summary>
   public void Open(Uri address) => _ = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
}
