/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Owns a verified staging file and prevents replacement until the native worker finishes.</summary>
public sealed class DownloadedDocument(string path, FileStream lease) : IDisposable
{
   public string Path { get; } = path;



   /// <summary>Releases the lease and then deletes the staging file. Deletion problems are swallowed because cleanup must not change an already committed spool receipt, and leftovers are removed at the next service start.</summary>
   public void Dispose()
   {
      lease.Dispose();
      try
      { File.Delete(Path); }
      catch(IOException) { /* Protected abandoned staging is removed before the next service run. */ }
      catch(UnauthorizedAccessException) { /* Cleanup failure must not change a committed spool receipt. */ }
   }
}
