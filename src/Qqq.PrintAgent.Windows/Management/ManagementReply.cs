/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// One reply on the management pipe: the contract version, whether the operation succeeded, and either its result or
/// its error. A reply carries the version of the request it answers, so a client never has to read a reply of another
/// version than it asked in; the one exception is <see cref="ManagementErrorCode.UnsupportedVersion"/>, which is
/// answered in this version whatever version was asked for. The result stays untyped here because its type depends on the operation;
/// <see cref="ManagementJson"/> reads it into the operation's own record. One result, that of a sign-out, carries the
/// identity provider's sign-out request, so neither the text form of a reply nor that of its result shows it.
/// <para>
/// A reply is built only by the <see cref="Success{TResult}"/> and <see cref="Failure(ManagementError)"/> functions,
/// which the parser uses too, and no member can be set afterwards. So every reply that exists is of this contract's
/// version and holds exactly one of the two: a success its result and no error, a failure its error and no result.
/// </para>
/// </summary>
[JsonConverter(typeof(ManagementJson.NotBySerializer<ManagementReply>))]
public sealed record ManagementReply
{
   /// <summary>Stores a reply. Only the functions below call this, each with exactly one of the two.</summary>
   private ManagementReply(ManagementPayload? result, ManagementError? error)
   {
      Result = result;
      Error = error;
   }



   /// <summary>The contract version the reply is in. It is always this contract's: a reply of another version is not read, and none can be built.</summary>
   public int V { get; } = ManagementProtocol.Version;



   /// <summary>True when the operation succeeded, which is when the reply holds a result and no error.</summary>
   public bool Ok => Error is null;



   /// <summary>The operation's result as the JSON object the wire carries; null exactly when the operation failed.</summary>
   public ManagementPayload? Result { get; }



   /// <summary>Why the operation failed; null exactly when it succeeded.</summary>
   public ManagementError? Error { get; }



   /// <summary>Builds the reply to an operation that succeeded, writing its result record the way the wire carries it. An operation with nothing to report answers <see cref="NoResult"/>, so a success always has a result object.</summary>
   public static ManagementReply Success<TResult>(TResult result)
      where TResult : class
      => Success(ManagementPayload.From(result));



   /// <summary>Builds the reply to an operation that succeeded around a result that is already the JSON object the wire carries: what the parser does with a success it read.</summary>
   public static ManagementReply Success(ManagementPayload result)
   {
      ArgumentNullException.ThrowIfNull(result);
      return new ManagementReply(result, null);
   }



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
      return new ManagementReply(null, error);
   }



   /// <summary>Says whether the operation succeeded and, if not, with which code. The result is never shown, so logging a reply cannot reveal the provider's sign-out request.</summary>
   public override string ToString() => Error is null ? $"Management reply v{V} ok (result not shown)" : $"Management reply v{V} failed: {Error.Code}";
}
