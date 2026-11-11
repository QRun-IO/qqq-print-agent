/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

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



   /// <summary>
   /// Builds the reply to an operation that failed with a code that carries no details. The message may repeat what a
   /// server said (the reason of <see cref="ManagementErrorCode.ServerRefused"/>, for one), so it is never sent as given:
   /// it goes through <see cref="QqqCompatibilityProblem.Printable"/>, the one definition Core uses for text a server
   /// supplied, and is cut at <see cref="ManagementProtocol.MaximumMessageCharacters"/>. A control character, a line
   /// separator or a character that reorders text therefore cannot reach a client's screen or log through any code.
   /// </summary>
   public static ManagementReply Failure(string code, string message) => new(ManagementProtocol.Version, false, Error: new ManagementError(code, Shown(message)));



   /// <summary>Builds the reply to an operation that failed with one of the codes that carry details, writing the details record the way the wire carries it. The message is made printable and cut exactly as in the other factory.</summary>
   public static ManagementReply Failure<TDetails>(string code, string message, TDetails details)
      where TDetails : class
      => new(ManagementProtocol.Version, false, Error: new ManagementError(code, Shown(message), ManagementPayload.From(details)));



   /// <summary>Says whether the operation succeeded and, if not, with which code. The result is never shown, so logging a reply cannot reveal the provider's sign-out request.</summary>
   public override string ToString() => Ok ? $"Management reply v{V} ok (result not shown)" : $"Management reply v{V} failed: {Error?.Code ?? "no error given"}";



   /// <summary>An error's message as it is sent: printable, and at most <see cref="ManagementProtocol.MaximumMessageCharacters"/> UTF-16 units before the mark of a cut.</summary>
   private static string Shown(string message) => QqqCompatibilityProblem.Printable(message, ManagementProtocol.MaximumMessageCharacters);
}
