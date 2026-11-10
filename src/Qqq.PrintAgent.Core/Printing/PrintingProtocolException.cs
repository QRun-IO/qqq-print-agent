/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The failure raised by the three readers that check what the server sends against the printing contract before any
/// field is trusted. The realtime frame reader raises it for a frame that is not valid JSON, repeats a field, lacks its
/// type, message identifier or payload object, for an ACK or ERROR without its required member, for a payload member
/// of the wrong JSON type, and for a realtime command that fails the command rule. The claim reader and the queued-job
/// list reader raise it for a response that is not valid JSON, lacks a required member, carries an unusable value, or
/// repeats a member the agent reads.
/// <para>
/// It does not cover the rest of the API client. The HTTP control poll and its receipt, enrollment, the operator
/// printer catalog, operator actions, realtime settings and document downloads report a bad response as
/// <see cref="InvalidDataException"/> or <see cref="System.Text.Json.JsonException"/>, as they did before this type
/// existed, and their callers in the Windows layer and the companion handle those types. In particular the command
/// rule is one rule, but it raises this type for a command that arrives over the realtime channel and
/// <see cref="InvalidDataException"/> for the same command in a polled batch.
/// </para>
/// <para>
/// It is an <see cref="IOException"/>, so every caller that already treats a broken connection as "reconnect and
/// replay" handles it without change, and a caller that wants to tell a misbehaving server from a lost one can catch
/// this type first. The message is fixed text naming what was wrong; it never repeats what the server sent.
/// </para>
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
