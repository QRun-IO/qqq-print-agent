/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Globalization;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// Opens an address in the browser for a sign-in or a provider sign-out, and makes a failure to do so safe to report.
/// The address carries a sign-in state or the provider's ID token hint. When Windows cannot start the browser, its
/// failure names the thing it could not start, which is that whole address, so the failure's own text must never
/// reach a log line or the screen.
/// </summary>
internal static class BrowserLaunch
{
   /// <summary>
   /// Opens the address. Any failure is replaced by one with fixed text that names only the kind of failure and,
   /// for a Windows error, its number. The new failure is thrown after the original has been let go, so the original
   /// is not attached as its cause: a report of the new failure, whichever way it is written out, then cannot
   /// contain the address.
   /// </summary>
   internal static void Open(IBrowserLauncher browser, Uri address)
   {
      string kind;
      try
      {
         browser.Open(address);
         return;
      }
      catch(Exception exception)
      {
         kind = exception is Win32Exception windows ? "Windows error " + windows.NativeErrorCode.ToString(CultureInfo.InvariantCulture) : exception.GetType().Name;
      }
      throw new IOException("The default browser could not be opened (" + kind + ").");
   }
}
