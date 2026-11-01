/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The durable record the worker depends on. Every state is written here before it is reported, and a reported event is
/// acknowledged here only after the server accepted it, so a crash or an outage can be recovered without losing an outcome
/// or printing a job twice. The service implements it with SQLite; tests supply an in-memory version.
/// </summary>
public interface IJobJournal
{
   /// <summary>Stores a claimed job's display facts (printer, title, format, creation time) so history stays readable.</summary>
   void TrackJob(PrintJob job, string? printerName);



   /// <summary>Returns the last recorded state of a job, or null when it is unknown, so a finished job is never printed again.</summary>
   string? GetState(string jobId);



   /// <summary>Appends an event and updates the job's state together, so the outbox and the visible state cannot disagree.</summary>
   void Record(string jobId, PrintEvent printEvent);



   /// <summary>Returns events not yet acknowledged by the server, oldest first and in bounded batches, for delivery.</summary>
   IReadOnlyList<(string JobId, PrintEvent Event)> Pending();



   /// <summary>Marks one event delivered, after the server accepted it.</summary>
   void Acknowledge(string eventId);



   /// <summary>Returns jobs recorded as received or submitting that never finished, so a restart can report them instead of leaving them unknown.</summary>
   IReadOnlyList<(string JobId, string State)> Incomplete();
}




