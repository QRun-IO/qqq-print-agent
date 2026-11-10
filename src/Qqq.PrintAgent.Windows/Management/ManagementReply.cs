/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// One reply on the management pipe: the contract version, whether the operation succeeded, and either its result or
/// its error. A reply carries the version of the request it answers, so a client never has to read a reply of another
/// version than it asked in. The result stays untyped here because its type depends on the operation;
/// <see cref="ManagementJson"/> reads it into the operation's own record. One result, that of a sign-out, carries the
/// identity provider's sign-out request, so neither the text form of a reply nor that of its result shows it.
/// </summary>
public sealed record ManagementReply(int V, bool Ok, ManagementPayload? Result = null, ManagementError? Error = null)
{
   /// <summary>Builds the reply to an operation that succeeded, writing its result record the way the wire carries it. An operation with nothing to report answers <see cref="NoResult"/>, so a success always has a result object.</summary>
   public static ManagementReply Success<TResult>(TResult result)
      where TResult : class
      => new(ManagementProtocol.Version, true, ManagementPayload.From(result));



   /// <summary>Builds the reply to an operation that failed with a code that carries no details.</summary>
   public static ManagementReply Failure(string code, string message) => new(ManagementProtocol.Version, false, Error: new ManagementError(code, message));



   /// <summary>Builds the reply to an operation that failed with one of the codes that carry details, writing the details record the way the wire carries it.</summary>
   public static ManagementReply Failure<TDetails>(string code, string message, TDetails details)
      where TDetails : class
      => new(ManagementProtocol.Version, false, Error: new ManagementError(code, message, ManagementPayload.From(details)));



   /// <summary>Says whether the operation succeeded and, if not, with which code. The result is never shown, so logging a reply cannot reveal the provider's sign-out request.</summary>
   public override string ToString() => Ok ? $"Management reply v{V} ok (result not shown)" : $"Management reply v{V} failed: {Error?.Code ?? "no error given"}";
}
