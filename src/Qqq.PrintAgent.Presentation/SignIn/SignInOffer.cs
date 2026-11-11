/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>
/// The sign-in methods on offer for one server. The service checks a server under its own account and the companion
/// reads the server's sign-in description as the signed-in user; a method is offered only when both found it, so a
/// button is never shown for a method that one of the two could not use.
/// </summary>
public sealed record SignInOffer(bool Password, bool Browser)
{
   /// <summary>True when at least one method is offered.</summary>
   public bool Any => Password || Browser;



   /// <summary>The methods the service's check found. A method name this companion does not know is ignored, as the contract asks of a client.</summary>
   public static SignInOffer Checked(ServerCheckResult check)
   {
      ArgumentNullException.ThrowIfNull(check);
      return new SignInOffer(check.SignInMethods.Contains(ServerCheckResult.Password, StringComparer.Ordinal), check.SignInMethods.Contains(ServerCheckResult.Browser, StringComparer.Ordinal));
   }



   /// <summary>
   /// The methods a sign-in description offers, by the rule the service's check applies: a table-based server takes
   /// a user name and password and nothing else, and any other server offers the browser when it publishes the native
   /// session grant. Whether that grant lists this application's callback is checked when the browser sign-in starts.
   /// </summary>
   public static SignInOffer Read(QqqAuthenticationMetadata description)
   {
      ArgumentNullException.ThrowIfNull(description);
      bool password = string.Equals(description.Type, "TABLE_BASED", StringComparison.Ordinal);
      return new SignInOffer(password, !password && description.Values?.NativeSessionLogin is not null);
   }



   /// <summary>The methods both this offer and another one hold.</summary>
   public SignInOffer Common(SignInOffer other)
   {
      ArgumentNullException.ThrowIfNull(other);
      return new SignInOffer(Password && other.Password, Browser && other.Browser);
   }



   /// <summary>The offer in words, for a log line: the names the contract gives the methods, or "no method".</summary>
   public override string ToString() => (Password, Browser) switch
   {
      (true, true) => ServerCheckResult.Password + " and " + ServerCheckResult.Browser,
      (true, false) => ServerCheckResult.Password,
      (false, true) => ServerCheckResult.Browser,
      _ => "no method",
   };
}
