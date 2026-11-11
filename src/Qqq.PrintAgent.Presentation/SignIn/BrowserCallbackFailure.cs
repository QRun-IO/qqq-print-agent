/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// What an exchange that waits for a browser callback is told when the callback inbox fails. Each exchange reports a
/// fixed list of failures as "not completed" and lets anything else through, as it would a programming error. The
/// inbox is fed by other processes, so it can fail outside that list: the real one fails with an argument error when
/// the process that sent a callback has already exited, which any process in the user's session can bring about. Such
/// a failure has to end a sign-in as a failure the operator is told about, in every exchange that waits, and not as
/// an exception nobody handles. So a failure outside the list is replaced by one inside it that names only the kind
/// of failure. Its own text is not passed on: nothing is known about what an unexpected failure says.
/// </summary>
internal static class BrowserCallbackFailure
{
   /// <summary>True for the kinds of inbox failure every exchange reports itself, with the failure's own text: a pipe that broke or was refused, a callback the inbox refused, and a wait that was ended.</summary>
   internal static bool Listed(Exception failure)
      => failure is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or OperationCanceledException;



   /// <summary>The failure to hand to an exchange: the inbox's own when it is of a listed kind, and otherwise one with fixed text and the kind of failure.</summary>
   internal static Exception Reported(Exception failure)
      => Listed(failure) ? failure : new IOException("The browser callback inbox failed (" + failure.GetType().Name + "), so no callback could be received. Try again.");
}
