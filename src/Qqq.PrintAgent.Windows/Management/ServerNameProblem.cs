/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What is wrong with a server's name, as <see cref="ServerName.Check"/> reports it, so the companion can tell the
/// operator which part of the rule the name breaks. The values are in the order the rule is checked: a name that breaks
/// it in several ways is reported under the first. The service does not send these; it refuses the request as an
/// invalid one, and the companion has already said why.
/// </summary>
public enum ServerNameProblem
{
   /// <summary>Nothing: the name may be a server's name.</summary>
   None,

   /// <summary>The name is missing, empty or only white space.</summary>
   Empty,

   /// <summary>The name is longer than <see cref="ServerName.MaximumCharacters"/> UTF-16 units once surrounding white space is removed.</summary>
   TooLong,

   /// <summary>The name holds a character that is not shown as itself (a line break or another control character, a character that reorders or hides text, a private-use or unassigned one), or a surrogate without its partner.</summary>
   NotPrintable,

   /// <summary>The name holds a square bracket.</summary>
   SquareBracket,

   /// <summary>The name is shaped like a server identifier or like the first eight or more characters of one, which the command line's <c>--server</c> would take for an identifier.</summary>
   LikeAnIdentifier,
}
