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
/// is recorded, and either can be made to fail the way the shell does when its automation object is unavailable.
/// </summary>
internal sealed class FakeShortcutShell : IShortcutShell
{
   /// <summary>Every shortcut written, in order, with the path it was written to.</summary>
   internal List<(string Path, ShortcutLink Link)> Written { get; } = [];



   /// <summary>Every path read, in order.</summary>
   internal List<string> Reads { get; } = [];



   /// <summary>When set, writing a shortcut fails with this.</summary>
   internal Exception? WriteFailure { get; set; }



   /// <summary>When set, reading a shortcut fails with this.</summary>
   internal Exception? ReadFailure { get; set; }



   /// <summary>Puts a shortcut file on disk as some other program would have, without recording it as written by the rules under test.</summary>
   internal static void Plant(string path, ShortcutLink link) => File.WriteAllText(path, JsonSerializer.Serialize(link));



   /// <summary>Reads what the file at the path says.</summary>
   public ShortcutLink Read(string path)
   {
      Reads.Add(path);
      if(ReadFailure is not null)
      { throw ReadFailure; }
      return JsonSerializer.Deserialize<ShortcutLink>(File.ReadAllText(path))!;
   }



   /// <summary>Writes the file and records what it says.</summary>
   public void Write(string path, ShortcutLink link)
   {
      if(WriteFailure is not null)
      { throw WriteFailure; }
      Written.Add((path, link));
      Plant(path, link);
   }
}
