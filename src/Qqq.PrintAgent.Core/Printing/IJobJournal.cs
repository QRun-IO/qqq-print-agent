/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

public interface IJobJournal
{
   string? GetState(string jobId);
   void Record(string jobId, PrintEvent printEvent);
   IReadOnlyList<(string JobId, PrintEvent Event)> Pending();
   void Acknowledge(string eventId);
}




