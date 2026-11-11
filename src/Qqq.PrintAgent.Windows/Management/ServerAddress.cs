/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// What a server's address may be: the one definition the service and the companion both call for the address an
/// operator types, where the rule used to be written out again wherever an address was read. It is the rule the session
/// client applies to the origin it is built for, so an address this accepts is one a session can be made for, and it
/// adds one thing the session client leaves to others: the host must have a plain ASCII name
/// (<see cref="QqqOrigin.Of"/>), because that name is what is stored, compared and shown. For the same reason an IPv6
/// literal with a zone (<c>[fe80::1%25eth0]</c>) is refused: the text of an origin has no place for the zone, so two
/// such addresses that differ only in it would be stored, compared and shown as one place. An address is accepted
/// when, after surrounding white space is removed:
/// <list type="bullet">
/// <item>it is an absolute address;</item>
/// <item>its scheme is HTTPS, or plain HTTP for this computer itself (a loopback host), which exists for development;</item>
/// <item>it carries no user information, no query and no fragment;</item>
/// <item>it has no path, since a server is its origin;</item>
/// <item>its host has an ASCII name, and is not an IPv6 literal with a zone.</item>
/// </list>
/// A caller that goes on to make a session for the server builds the session client from the origin this returns,
/// never from the text the operator typed: for a host written outside ASCII the two differ (the text keeps the
/// Unicode name, the origin has the name the connection is made to), and only the origin is what was judged, and what
/// is stored and shown.
/// </summary>
public static class ServerAddress
{
   /// <summary>
   /// Reads the address an operator typed. On success it returns the server's origin in the one form an origin is
   /// kept in (scheme, the host's ASCII name and port, with the path "/"); otherwise it says what is wrong, reporting
   /// the first problem in the order of <see cref="ServerAddressProblem"/>. Nothing is logged or thrown, whatever the
   /// text holds.
   /// </summary>
   public static bool TryRead(string? text, [NotNullWhen(true)] out Uri? origin, out ServerAddressProblem problem)
   {
      Uri? address = null;
      string trimmed = text?.Trim() ?? "";
      problem = trimmed.Length == 0 ? ServerAddressProblem.Empty
         : !Uri.TryCreate(trimmed, UriKind.Absolute, out address) ? ServerAddressProblem.NotAnAddress
         : Judge(address);
      origin = problem == ServerAddressProblem.None ? QqqOrigin.Of(address!) : null;
      return origin is not null;
   }



   /// <summary>Says what is wrong with an absolute address as a server's address, or nothing.</summary>
   private static ServerAddressProblem Judge(Uri address)
   {
      bool secure = string.Equals(address.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);
      bool thisComputer = string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && address.IsLoopback;
      return !secure && !thisComputer ? ServerAddressProblem.NotSecure
         : address.UserInfo.Length != 0 ? ServerAddressProblem.HasUserInformation
         : address.Query.Length != 0 || address.Fragment.Length != 0 ? ServerAddressProblem.HasQueryOrFragment
         : !string.Equals(address.AbsolutePath, "/", StringComparison.Ordinal) ? ServerAddressProblem.HasPath
         : QqqOrigin.Of(address) is not { } origin || HasZone(origin) ? ServerAddressProblem.HostNotNameable
         : ServerAddressProblem.None;
   }



   /// <summary>
   /// True when an origin's host is an IPv6 literal with a zone. The origin form keeps the zone in the host it
   /// connects to and drops it from its text, which is the form the contract stores, compares and shows; an origin
   /// like that cannot be named by its text. Only an IPv6 literal can hold a percent sign in the host of an origin
   /// (<see cref="QqqOrigin.Of"/>).
   /// </summary>
   internal static bool HasZone(Uri origin) => origin.IdnHost.Contains('%', StringComparison.Ordinal);
}
