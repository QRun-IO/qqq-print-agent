/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The fixed numbers of version 2 of the management pipe (ADR-0004): the version the envelope carries, how many clients
/// are served at once, how many servers a computer may hold, and how large one request and one reply may be. They are in
/// one place so the service, the command line and the companion cannot disagree about them.
/// </summary>
public static class ManagementProtocol
{
   /// <summary>The value of <c>v</c> in every request and reply of this contract.</summary>
   public const int Version = 2;



   /// <summary>How many clients the pipe serves at once, so a sign-in that takes two minutes does not make status unavailable. A seventeenth client waits for a free instance.</summary>
   public const int MaximumConcurrentClients = 16;



   /// <summary>How many servers one computer may hold. Adding another is refused with <see cref="ManagementErrorCode.ServerLimit"/>.</summary>
   public const int MaximumServers = 8;



   /// <summary>The most characters one request may have. It is the existing framing limit for a command, so a version 2 request fits the same frame as a version 1 request.</summary>
   public const int MaximumRequestCharacters = AgentPipeFraming.MaximumCommandCharacters;



   /// <summary>The most characters one reply may have: the existing framing limit for a response.</summary>
   public const int MaximumReplyCharacters = AgentPipeFraming.MaximumResponseCharacters;



   /// <summary>Writes a server's local identity the one way it appears on the wire: the GUID's canonical form, lower-case with hyphens.</summary>
   public static string FormatServerId(Guid serverId) => serverId.ToString("D");



   /// <summary>
   /// True for text that is a server identifier exactly as <see cref="FormatServerId"/> writes it. Upper-case letters,
   /// braces, a missing hyphen and surrounding spaces are all refused, so one server has one spelling and an identifier
   /// can be compared as text.
   /// </summary>
   public static bool IsServerId([NotNullWhen(true)] string? value)
      => value is not null && Guid.TryParseExact(value, "D", out Guid parsed) && string.Equals(FormatServerId(parsed), value, StringComparison.Ordinal);
}
