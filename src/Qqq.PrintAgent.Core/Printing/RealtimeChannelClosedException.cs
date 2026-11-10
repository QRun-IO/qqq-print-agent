/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The failure a send gets because the realtime connection was already gone: a send that was still waiting for its
/// acknowledgement when the channel closed, or a send attempted afterwards. It reports a consequence, not a cause. What
/// closed the channel is the inner exception, when the reader or writer ended with a failure and the send was waiting at
/// that moment. It has a type of its own so that whoever chooses the reason a connection ended can tell it from an I/O
/// failure that is a reason in itself, such as a full output queue. It derives from <see cref="IOException"/>, so code that
/// handles a lost connection by catching that keeps working.
/// </summary>
public sealed class RealtimeChannelClosedException(string message, Exception? cause) : IOException(message, cause);
