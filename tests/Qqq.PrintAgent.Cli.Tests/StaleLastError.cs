/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.InteropServices;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The marker the printing fakes leave as the thread's last error at the start of every call that stands for a native
/// import declared with SetLastError. Such an import replaces the thread's last error each time it is called, whether it
/// succeeds or fails, so an error is only there to be read straight after the call that failed. The fakes behave the
/// same way: a call that fails the way a test arranged then sets the arranged code, and every other such call leaves
/// this marker. A rule that reads the error one call late (after a close, an abort or any other call that records an
/// error) therefore gets the marker and not the arranged code, and the test that expects the code fails. A fake member
/// that stands for an import without SetLastError does not touch the last error at all, as the real call does not.
/// </summary>
internal static class StaleLastError
{
   /// <summary>
   /// 0x0BADC0DE (195,936,478). No test arranges it as a Windows error; seeing it as the code of a failure means the error
   /// was read after another call had replaced it, or was read for a call that records no error.
   /// </summary>
   internal const int Code = 0x0BAD_C0DE;



   /// <summary>Replaces the thread's last error with the marker, as the start of a call that records its error replaces whatever an earlier call left.</summary>
   internal static void Leave() => Marshal.SetLastPInvokeError(Code);
}
