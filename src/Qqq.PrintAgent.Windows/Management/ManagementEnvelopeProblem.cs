/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What was wrong with the text of a request or a reply before its operation was even looked at. Each case is told
/// apart because each needs a different answer: a request without a version comes from a version 1 client and gets the
/// one legacy reply; an unknown version gets <see cref="ManagementErrorCode.UnsupportedVersion"/>; everything else is
/// <see cref="ManagementErrorCode.InvalidRequest"/>, and the service log says which.
/// </summary>
public enum ManagementEnvelopeProblem
{
   /// <summary>Nothing: the envelope was read.</summary>
   None,

   /// <summary>The text is longer than the contract allows; it was not parsed.</summary>
   TooLarge,

   /// <summary>The text is empty or is not JSON.</summary>
   NotJson,

   /// <summary>The text is JSON, but one of its objects has the same member twice: in the envelope, in the arguments or anywhere beneath them. Readers disagree about which of two values counts, so neither is taken.</summary>
   RepeatedMember,

   /// <summary>The text is JSON but its root is not an object.</summary>
   NotAnObject,

   /// <summary>The object has no <c>v</c>. A request like this comes from a version 1 client.</summary>
   VersionMissing,

   /// <summary>The object has a <c>v</c> that is not this contract's version, or is not a whole number.</summary>
   VersionUnsupported,

   /// <summary>A request has a member the envelope does not define.</summary>
   UnexpectedMember,

   /// <summary>A request has no <c>op</c>, or one that is not text.</summary>
   OperationMissing,

   /// <summary>A request's <c>op</c> does not have the shape of an operation name: lower-case letters, optionally a dot and a camel-case word.</summary>
   OperationMalformed,

   /// <summary>A request's <c>server</c> is not a server identifier in its canonical form.</summary>
   ServerMalformed,

   /// <summary>A request's <c>args</c> is not an object.</summary>
   ArgumentsNotAnObject,

   /// <summary>A reply lacks what its <c>ok</c> promises: a result object for a success, or an error with a code and a message for a failure; or it has no <c>ok</c>.</summary>
   ReplyIncomplete,
}
