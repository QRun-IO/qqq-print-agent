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
   /// <summary>What an operator can do about a browser that could not be opened; it follows the kind of failure in the failure's text, which is shown as the reason a sign-in was not completed, and it ends what the dialog says about a provider sign-out whose page could not be opened.</summary>
   internal const string Remedy = "Check that a default browser is set in Windows Settings under Apps, Default apps, then try again.";



   /// <summary>
   /// Opens the address. Any failure is replaced by one with fixed text: that the default browser could not be
   /// opened, the kind of failure (for a Windows error, its number) and what to do about it. The new failure is
   /// thrown after the original has been let go, so the original is not attached as its cause: a report of the new
   /// failure, whichever way it is written out, then cannot contain the address.
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
      throw new IOException("The default browser could not be opened (" + kind + "). " + Remedy);
   }
}
