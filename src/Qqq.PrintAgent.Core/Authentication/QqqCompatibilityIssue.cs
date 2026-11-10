/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// One reason a server cannot be used by this agent, as a value the connection wizard can branch on. The log has always
/// carried these reasons as text; this is the same knowledge in a form a screen can act on without parsing a message.
/// </summary>
public enum QqqCompatibilityIssue
{
   /// <summary>No answer at all: the name did not resolve, the connection was refused or reset, or the request timed out.</summary>
   ServerUnreachable,

   /// <summary>Something answered, but the secure connection could not be established: an untrusted, mismatched or missing certificate, or a failed TLS handshake.</summary>
   SecureConnectionFailed,

   /// <summary>The address answered over HTTP, but not with a usable QQQ discovery document: an error status, a redirect, a sign-in page, or malformed JSON.</summary>
   NotQqqServer,

   /// <summary>A QQQ server whose UI API version is not a plain release this agent supports.</summary>
   ApiVersionUnsupported,

   /// <summary>A QQQ server that lacks a core feature this agent calls.</summary>
   ApiFeatureMissing,

   /// <summary>A QQQ server that does not offer the <c>api</c> client channel this agent identifies itself on.</summary>
   ClientChannelMissing,

   /// <summary>A QQQ server without the printing extension.</summary>
   PrintingExtensionMissing,

   /// <summary>A QQQ server whose printing extension predates realtime printing, so it could accept a sign-in but never deliver a job.</summary>
   PrintingExtensionTooOld,

   /// <summary>A printing extension this agent cannot negotiate with: a newer major version, a version that is not a plain release, a non-standard path, or more than one extension.</summary>
   PrintingExtensionUnsupported,

   /// <summary>The server is otherwise usable but offers no sign-in method this agent implements, or its sign-in description could not be read.</summary>
   SignInUnavailable,
}
