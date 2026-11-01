/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent;

/// <summary>Leaves a failed native layout unhandled after AppKit has recorded it.</summary>
internal static class UiExceptionPolicy
{
   internal static void HandleUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
   {
      string? exceptionType;
      try
      {
         exceptionType = args.Exception?.GetType().FullName;
      }
      catch(Exception)
      {
         // A stowed native error can lose its exception metadata. Preserve
         // AppKit's existing policy when this specific failure cannot be proven.
         return;
      }
      // Continuing after this native failure left the companion unable to accept input.
      // Normal WinUI termination affects only this process; the print service remains alive.
      if(string.Equals(exceptionType, "Microsoft.UI.Xaml.LayoutCycleException", StringComparison.Ordinal))
      {
         args.Handled = false;
      }
   }
}
