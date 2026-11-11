/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// One reply on the management pipe: the contract version, whether the operation succeeded, and either its result or
/// its error. A reply carries the version of the request it answers, so a client never has to read a reply of another
/// version than it asked in. The result stays untyped here because its type depends on the operation;
/// <see cref="ManagementJson"/> reads it into the operation's own record. One result, that of a sign-out, carries the
/// identity provider's sign-out request, so neither the text form of a reply nor that of its result shows it.
/// </summary>
[JsonConverter(typeof(ManagementJson.NotBySerializer<ManagementReply>))]
public sealed record ManagementReply(int V, bool Ok, ManagementPayload? Result = null, ManagementError? Error = null)
{
   /// <summary>Builds the reply to an operation that succeeded, writing its result record the way the wire carries it. An operation with nothing to report answers <see cref="NoResult"/>, so a success always has a result object.</summary>
   public static ManagementReply Success<TResult>(TResult result)
      where TResult : class
      => new(ManagementProtocol.Version, true, ManagementPayload.From(result));



   /// <summary>
   /// Builds the reply to an operation that failed with a code that carries no details. The message may repeat what a
   /// server said (the reason of <see cref="ManagementErrorCode.ServerRefused"/>, for one); <see cref="ManagementError"/>
   /// makes it printable and cuts it, here as for an error built any other way, and refuses a code that is not in the
   /// table or that carries details.
   /// </summary>
   public static ManagementReply Failure(string code, string message) => Failure(new ManagementError(code, message));



   /// <summary>Builds the reply to an operation that failed with one of the codes that carry details, writing the details record the way the wire carries it. The record must be the one the table names for the code (<see cref="ManagementError.WithDetails"/>).</summary>
   public static ManagementReply Failure<TDetails>(string code, string message, TDetails details)
      where TDetails : class
      => Failure(ManagementError.WithDetails(code, message, details));



   /// <summary>Builds the reply that carries an error that already exists, such as the refusal <see cref="ManagementOperations.Check"/> returns, so no failure has to be sent by building a reply member by member.</summary>
   public static ManagementReply Failure(ManagementError error)
   {
      ArgumentNullException.ThrowIfNull(error);
      return new ManagementReply(ManagementProtocol.Version, false, Error: error);
   }



   /// <summary>Says whether the operation succeeded and, if not, with which code. The result is never shown, so logging a reply cannot reveal the provider's sign-out request.</summary>
   public override string ToString() => Ok ? $"Management reply v{V} ok (result not shown)" : $"Management reply v{V} failed: {Error?.Code ?? "no error given"}";
}
