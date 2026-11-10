/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// A management request failed before any of its bytes were written to the pipe: the service did not accept a connection in
/// time, the pipe could not be opened, the other end was not verified as the installed service, or the request was too
/// large to send. It tells a caller for certain that the service did not act, which the original failure (kept as the
/// inner exception, with the same message) cannot: a time limit or a broken pipe can also happen after the request left.
/// </summary>
public sealed class AgentPipeNotSentException(Exception cause) : IOException((cause ?? throw new ArgumentNullException(nameof(cause))).Message, cause);
