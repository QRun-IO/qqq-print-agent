/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Cryptography;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A made-up document staged as a real file in a temporary folder of its own, the way the service hands a verified
/// download to the printer adapter, with the job that describes it. The folder is deleted when the test is done. The
/// bytes are synthetic and are never sent to a printer.
/// </summary>
internal sealed class StagedDocument : IDisposable
{
   private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qqq-staged-document-" + Guid.NewGuid().ToString("N"));



   /// <summary>Writes the bytes to a new file in a new temporary folder.</summary>
   internal StagedDocument(byte[] content)
   {
      Content = content;
      Directory.CreateDirectory(_directory);
      Path = System.IO.Path.Combine(_directory, "document.bin");
      File.WriteAllBytes(Path, content);
   }



   /// <summary>The absolute path of the staged file.</summary>
   internal string Path { get; }



   /// <summary>The bytes that were staged.</summary>
   internal byte[] Content { get; }



   /// <summary>A job for these exact bytes (their length and SHA-256) on the given printer, in the given format.</summary>
   internal PrintJob Job(string printerId, string format, PrintOptions? options = null)
      => new("job-1", printerId, "Synthetic document", format, Convert.ToHexString(SHA256.HashData(Content)), Content.Length, options ?? new PrintOptions(), "2026-11-01T00:00:00Z", "QUEUED");



   /// <summary>Deletes the temporary folder and the file in it.</summary>
   public void Dispose() => Directory.Delete(_directory, recursive: true);
}
