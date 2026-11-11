/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// What the worker needs from a printing platform: read the printers it can use and submit one staged document. Windows, the isolated
/// helper, the exposure filter, the profile wrapper and test fakes all implement it, so the worker's safety rules are written once. A
/// document reaches a backend only as a verified file, so no document travels in memory or in a control message, and every backend,
/// a wrapper around another included, can take one.
/// <para>
/// There are three reads, and they differ in what they cost the platform. <see cref="Discover"/> reads the settings of every
/// printer; <see cref="ListWithoutMedia"/> reads the settings of none; <see cref="FindWithMedia"/> reads the settings of one. A
/// printer whose settings cannot be read (a driver that hangs or fails) therefore gets in the way only of a read that asks for
/// its settings. A print job uses the second before it is claimed and the third for its own printer, and never the first.
/// </para>
/// <para>
/// That is a statement about settings and no more. All three reads begin with the platform's own list of printers, and what
/// the platform does to produce that list is outside this contract. On Windows a connection to a printer on a print server
/// that does not answer can hold the list, and so all three reads; see the Windows adapter.
/// </para>
/// </summary>
public interface IPrinterBackend
{
   /// <summary>
   /// Lists every printer this backend can print to, with its formats, status and media profiles. It reads the settings of every
   /// printer, so it is as slow as the slowest one and fails when one of them cannot be read at all. It is for reporting the
   /// inventory, never for a print job.
   /// </summary>
   IReadOnlyList<PrinterInfo> Discover();



   /// <summary>
   /// Lists every printer this backend can print to by what identifies it: identifier, names, port, status and formats, exactly
   /// as <see cref="Discover"/> reports them, with no media profiles (<see cref="PrinterInfo.MediaProfiles"/> is null, meaning
   /// "not read"). It reads the settings of no printer, so a printer whose settings cannot be read does not slow it or fail
   /// it on that account. It is still as slow as the platform's own list of printers, and fails when that list fails.
   /// </summary>
   IReadOnlyList<PrinterInfo> ListWithoutMedia();



   /// <summary>
   /// The one printer with this identifier as it is now, exactly as <see cref="Discover"/> reports it, media profiles included,
   /// or null when this backend has no such printer. It reads the settings of that printer and of no other.
   /// </summary>
   PrinterInfo? FindWithMedia(string printerId);



   /// <summary>
   /// Prints the verified file at the path on the job's printer and returns the spooler receipt. The caller keeps its read-only
   /// lease on the file until this returns, so the content cannot change underneath. The limit bounds the native submission where
   /// the backend can enforce one. A failure after submission began, a timeout included, means the outcome is uncertain.
   /// </summary>
   Task<string> PrintFileAsync(PrintJob job, string path, TimeSpan submissionTimeout, CancellationToken cancellationToken);
}
