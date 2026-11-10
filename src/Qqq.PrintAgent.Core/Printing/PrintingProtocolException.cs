/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The one failure raised when the server sends something that breaks the printing contract: a realtime frame, a claimed
/// job or a queued-job list that is not valid JSON, repeats a field, lacks a required member or carries an unusable
/// value. It is an <see cref="IOException"/>, so every caller that already treats a broken connection as "reconnect and
/// replay" handles it without change, and a caller that wants to tell a misbehaving server from a lost one can catch
/// this type first. The message is fixed text naming what was wrong; it never repeats what the server sent.
/// </summary>
public sealed class PrintingProtocolException : IOException
{
   /// <summary>Reports a violation that cannot be tied to one job.</summary>
   public PrintingProtocolException(string message) : base(message)
   {
   }



   /// <summary>Reports a claimed job that broke the contract but named itself well enough to be reported back to the server.</summary>
   public PrintingProtocolException(string message, PrintJob rejectedJob) : base(message)
   {
      RejectedJob = rejectedJob;
   }



   /// <summary>
   /// A stand-in for the claimed job that was refused, holding only its identifier and the display facts that were safe
   /// to keep, or null when the violation is not about an identifiable job. The worker uses it to record the job as
   /// FAILED, because a claimed job the agent never answers would be offered again forever and block the queue behind it.
   /// </summary>
   public PrintJob? RejectedJob { get; }
}
