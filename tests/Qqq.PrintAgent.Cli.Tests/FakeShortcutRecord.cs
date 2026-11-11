/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The note of the last shortcut, held in memory instead of the registry, with every access recorded.</summary>
internal sealed class FakeShortcutRecord : IShortcutRecord
{
   /// <summary>The recorded file name, or null when none is recorded.</summary>
   internal string? Name { get; set; }



   /// <summary>For each read, whether it asked for the store to be created.</summary>
   internal List<bool> Reads { get; } = [];



   /// <summary>Every name written, in order.</summary>
   internal List<string> Writes { get; } = [];



   /// <summary>How many times the note was cleared.</summary>
   internal int Cleared { get; private set; }



   /// <summary>Returns the recorded name and notes how it was asked for.</summary>
   public string? ReadLastShortcutName(bool createStore)
   {
      Reads.Add(createStore);
      return Name;
   }



   /// <summary>Records the name.</summary>
   public void WriteLastShortcutName(string filename)
   {
      Writes.Add(filename);
      Name = filename;
   }



   /// <summary>Forgets the name.</summary>
   public void ClearLastShortcutName()
   {
      Cleared++;
      Name = null;
   }
}
