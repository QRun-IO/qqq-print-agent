/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Shortcut files without the Windows shell. A shortcut is written as a small text file that holds what the shortcut says,
/// so it survives being moved or replaced like the real file does, and is read back from that text. Every read and write
/// is recorded. Reading fails or finds no shortcut where the real shell would, and can also be made to fail; writing can be made to fail either before any file exists or part way,
/// leaving a broken file behind as the real shell can. A test can also act between the steps of the rules under test,
/// as another program on the same computer could.
/// </summary>
internal sealed class FakeShortcutShell : IShortcutShell
{
   /// <summary>Every shortcut written, in order, with the path it was written to.</summary>
   internal List<(string Path, ShortcutLink Link)> Written { get; } = [];



   /// <summary>Every path read, in order.</summary>
   internal List<string> Reads { get; } = [];



   /// <summary>Every path at which a failed write left a broken file behind.</summary>
   internal List<string> Abandoned { get; } = [];



   /// <summary>When set, writing a shortcut fails with this.</summary>
   internal Exception? WriteFailure { get; set; }



   /// <summary>Whether a failing write gives up before it has created any file. By default it fails part way and leaves a broken file.</summary>
   internal bool WriteFailsBeforeAnyFile { get; set; }



   /// <summary>When set, reading a shortcut fails with this.</summary>
   internal Exception? ReadFailure { get; set; }



   /// <summary>When set, only reading this path fails; every other shortcut can still be read.</summary>
   internal string? ReadFailureOn { get; set; }



   /// <summary>Something that happens just before the shell writes: a test uses it to see what was recorded by then, or to change a file as another program would.</summary>
   internal Action? BeforeWrite { get; set; }



   /// <summary>Something that happens just after a failing write left its broken file behind, given that file's path: a test uses it to take hold of the file as another program would.</summary>
   internal Action<string>? AfterAbandoning { get; set; }



   /// <summary>Something that happens just after the shell read a shortcut: a test uses it to change a file between two looks at it.</summary>
   internal Action? AfterRead { get; set; }



   /// <summary>Puts a shortcut file on disk as some other program would have, without recording it as written by the rules under test.</summary>
   internal static void Plant(string path, ShortcutLink link) => File.WriteAllText(path, JsonSerializer.Serialize(link));



   /// <summary>
   /// Reads what the file at the path says, as the real shell does: the file is opened for reading, shared for reading
   /// only, so a file another handle holds exclusively fails here as it does on Windows; a file that opens and does
   /// not hold a shortcut (an empty file, or anything that is not this fake's text) is null.
   /// </summary>
   public ShortcutLink? Read(string path)
   {
      Reads.Add(path);
      if(ReadFailure is not null && (ReadFailureOn is null || string.Equals(ReadFailureOn, path, StringComparison.OrdinalIgnoreCase)))
      { throw ReadFailure; }
      ShortcutLink? link;
      using(var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)))
      {
         try
         { link = JsonSerializer.Deserialize<ShortcutLink>(reader.ReadToEnd()); }
         catch(JsonException)
         { link = null; }
      }
      AfterRead?.Invoke();
      return link;
   }



   /// <summary>Writes the file and records what it says, or fails as asked, leaving half a file unless told to fail earlier.</summary>
   public void Write(string path, ShortcutLink link)
   {
      BeforeWrite?.Invoke();
      if(WriteFailure is not null)
      {
         if(!WriteFailsBeforeAnyFile)
         {
            string whole = JsonSerializer.Serialize(link);
            File.WriteAllText(path, whole[..(whole.Length / 2)]);
            Abandoned.Add(path);
            AfterAbandoning?.Invoke(path);
         }
         throw WriteFailure;
      }
      Written.Add((path, link));
      Plant(path, link);
   }
}
