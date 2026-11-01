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
   /// <summary>True only when the exact window handle still exists, belongs to the expected process, and its visibility equals the expected value. A recycled handle owned by another process, or a destroyed window, therefore never counts as a match.</summary>
   internal static bool Matches(nint window, int processId, bool visible)
   {
      if(!IsWindow(window))
      {
         return false;
      }
      _ = User32.GetWindowThreadProcessId(window, out uint owner);
      return owner == processId && IsWindowVisible(window) == visible;
   }



   /// <summary>Win32 check that a window handle still refers to a live window, loaded from the system folder only so a planted DLL cannot replace it.</summary>
   [LibraryImport("user32.dll")]
   [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static partial bool IsWindow(nint window);



   /// <summary>Win32 check of a window's visible state (the tray-hidden window reports false), loaded from the system folder only.</summary>
   [LibraryImport("user32.dll")]
   [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
   [return: MarshalAs(UnmanagedType.Bool)]
   private static partial bool IsWindowVisible(nint window);
}
