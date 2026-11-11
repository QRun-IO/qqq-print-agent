/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// A shortcut file that another program holds: a virus scanner, a backup or the Start menu itself can have one open for
/// a moment. This part holds the patience for that (a file step is tried again, a bounded number of times, while Windows
/// says the file is in use), the words for the kind of a failure, and what a run notes when it left such a file where
/// it is. The installer runs the branding run and the uninstall as required steps, so a shortcut that stays held is
/// left alone and noted where that is safe, instead of failing the installer; where it is safe is decided in the other
/// parts.
/// </summary>
internal sealed partial class AgentBrandingShortcuts
{
   /// <summary>How often a file step is tried while the file is in use: once, and then again after each of eight pauses.</summary>
   internal const int HeldAttempts = 9;



   /// <summary>Windows' whole result code for a file another program has open in a way that does not allow the step.</summary>
   private const int SharingViolation = unchecked((int)0x80070020);



   /// <summary>Windows' whole result code for a part of a file another program has locked.</summary>
   private const int LockViolation = unchecked((int)0x80070021);



   /// <summary>The pause between two tries. Eight of them are two seconds, which is how long one held file can delay a run at one step.</summary>
   internal static readonly TimeSpan HeldPause = TimeSpan.FromMilliseconds(250);



   /// <summary>
   /// Whether a failure says the file is in use: a sharing violation or a lock violation, told by Windows' whole result
   /// code. Access denied is not in use, and neither is any other input or output failure; those are not tried again.
   /// </summary>
   internal static bool InUse(Exception exception) => exception is IOException && exception.HResult is SharingViolation or LockViolation;



   /// <summary>
   /// Runs one file step, and runs it again after a pause for as long as it fails because the file is in use, up to
   /// <see cref="HeldAttempts"/> tries in all. The step is run whole each time, so a step that looks at a file before
   /// it changes it looks again on every try. The failure of the last try, and any failure that is not about a file in
   /// use, is passed on at once. The pause is the one the rules were given, so a test waits for nothing.
   /// </summary>
   private void WhileHeld(Action step)
   {
      for(int attempt = 1; ; attempt++)
      {
         try
         {
            step();
            return;
         }
         catch(IOException exception) when(InUse(exception) && attempt < HeldAttempts)
         {
            wait(HeldPause);
         }
      }
   }



   /// <summary>
   /// The kind of a failure, in words that are safe to log: never the failure's own text, which names full paths. A
   /// file in use says which of the two violations it was; anything else from the file system gives its type and
   /// Windows' result code.
   /// </summary>
   internal static string Kind(Exception exception) => exception switch
   {
      IOException when exception.HResult == SharingViolation => "in use by another program: sharing violation",
      IOException when exception.HResult == LockViolation => "in use by another program: lock violation",
      UnauthorizedAccessException => "access denied",
      InvalidOperationException => "Windows shortcut automation failed",
      _ => exception.GetType().Name + " 0x" + exception.HResult.ToString("X8", CultureInfo.InvariantCulture),
   };



   /// <summary>The note of an uninstall that left a recorded shortcut behind.</summary>
   private static string LeftBehind(string name, string kind)
      => $"The recorded shortcut \"{name}\" could not be removed ({kind}). It was left where it is and is no longer recorded: delete it by hand, or install again under the same name, which takes it over.";
}
