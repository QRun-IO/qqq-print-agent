/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What is wrong with a server's address, as <see cref="ServerAddress.TryRead"/> reports it, so the companion can tell
/// the operator what to correct. The values are in the order the rule is checked: an address that breaks it in several
/// ways is reported under the first.
/// </summary>
public enum ServerAddressProblem
{
   /// <summary>Nothing: the address names a server's origin.</summary>
   None,

   /// <summary>The address is missing, empty or only white space.</summary>
   Empty,

   /// <summary>The text is not an absolute address: it has no scheme, or cannot be read as one.</summary>
   NotAnAddress,

   /// <summary>The address is not HTTPS. Plain HTTP is accepted for this computer itself only, for development.</summary>
   NotSecure,

   /// <summary>The address carries a user name or a password, which must never be part of a stored or logged address.</summary>
   HasUserInformation,

   /// <summary>The address has a query or a fragment.</summary>
   HasQueryOrFragment,

   /// <summary>The address has a path. A server is named by its origin: scheme, host and port.</summary>
   HasPath,

   /// <summary>The address's host has no plain ASCII name, so it cannot be compared or shown as the place a connection is really made to.</summary>
   HostNotNameable,
}
