/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The record of the shortcuts this installer may own, held in memory instead of the registry. Every access is written
/// down in order, and the record can be made to refuse change, or to fail when the current name is written, the way the
/// registry can.
/// </summary>
internal sealed class FakeShortcutRecord : IShortcutRecord
{
   /// <summary>The recorded name of the current shortcut, or null when none is recorded.</summary>
   internal string? Name { get; set; }



   /// <summary>The recorded names of the other shortcuts.</summary>
   internal List<string> Pending { get; } = [];



   /// <summary>
   /// Every access in order: "require", "read-last", "read-pending", "last=" with the name written, "pending=" with the
   /// names written joined by a bar, "clear-last" and "clear-pending".
   /// </summary>
   internal List<string> Operations { get; } = [];



   /// <summary>The accesses that changed the record, in order: everything but the proof of changeability and the reads.</summary>
   internal IEnumerable<string> Changes => Operations.Where(operation => operation is not ("require" or "read-last" or "read-pending"));



   /// <summary>When set, the record cannot be changed: proving changeability fails with this.</summary>
   internal Exception? ChangeRefusal { get; set; }



   /// <summary>When set, writing the name of the current shortcut fails with this and records nothing.</summary>
   internal Exception? WriteLastFailure { get; set; }



   /// <summary>Notes that it was asked, and refuses when the test said the record cannot be changed.</summary>
   public void RequireChangeable()
   {
      Operations.Add("require");
      if(ChangeRefusal is not null)
      { throw ChangeRefusal; }
   }



   /// <summary>Returns the recorded name of the current shortcut.</summary>
   public string? ReadLastShortcutName()
   {
      Operations.Add("read-last");
      return Name;
   }



   /// <summary>Returns a copy of the other recorded names.</summary>
   public IReadOnlyList<string> ReadPendingShortcutNames()
   {
      Operations.Add("read-pending");
      return [.. Pending];
   }



   /// <summary>Records the name of the current shortcut, or fails as asked.</summary>
   public void WriteLastShortcutName(string filename)
   {
      Operations.Add("last=" + filename);
      if(WriteLastFailure is not null)
      { throw WriteLastFailure; }
      Name = filename;
   }



   /// <summary>Replaces the other recorded names; an empty list is refused, as the contract says it is never passed.</summary>
   public void WritePendingShortcutNames(IReadOnlyList<string> filenames)
   {
      Operations.Add("pending=" + string.Join('|', filenames));
      if(filenames.Count == 0)
      { throw new ArgumentException("The rules must clear the other names instead of writing an empty list.", nameof(filenames)); }
      Pending.Clear();
      Pending.AddRange(filenames);
   }



   /// <summary>Forgets the name of the current shortcut.</summary>
   public void ClearLastShortcutName()
   {
      Operations.Add("clear-last");
      Name = null;
   }



   /// <summary>Forgets the other names.</summary>
   public void ClearPendingShortcutNames()
   {
      Operations.Add("clear-pending");
      Pending.Clear();
   }
}
