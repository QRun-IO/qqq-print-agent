/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;
using FlaUI.Core.WindowsAPI;


namespace Qqq.PrintAgent.UiTests;

/// <summary>Checks HWND lifetime and visibility without relying on a hidden UIA provider.</summary>
internal static partial class NativeWindowState
{
   internal static bool Matches(nint window, int processId, bool visible)
   {
      if(!IsWindow(window))
      {
         return false;
      }
      _ = User32.GetWindowThreadProcessId(window, out uint owner);
      return owner == processId && IsWindowVisible(window) == visible;
   }

   [LibraryImport("user32.dll")]
   [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static partial bool IsWindow(nint window);

   [LibraryImport("user32.dll")]
   [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static partial bool IsWindowVisible(nint window);
}
