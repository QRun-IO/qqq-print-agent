/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>
/// A browser that cannot be started, failing the way Windows fails: the failure's own text names what could not be
/// started, which is the whole address the browser was asked to open. The addresses here carry marked synthetic values
/// in the places a real sign-in address and a real sign-out address carry their sensitive ones (state, nonce, code
/// challenge, ID token hint), so a test can show that none of them, and no query at all, reaches a log line, a message
/// or a property a view binds to.
/// </summary>
internal static class BrowserStartFailure
{
   /// <summary>The marked state of both addresses.</summary>
   internal const string State = "synthetic-state-canary";

   /// <summary>The marked nonce of the sign-in address.</summary>
   internal const string Nonce = "synthetic-nonce-canary";

   /// <summary>The marked code challenge of the sign-in address.</summary>
   internal const string Challenge = "synthetic-challenge-canary";

   /// <summary>The marked ID token hint of the sign-out address.</summary>
   internal const string Hint = "synthetic-id-token-hint-canary";

   /// <summary>What an operator is told to do, which ends every report of a browser that could not be opened.</summary>
   internal const string Remedy = "Check that a default browser is set in Windows Settings under Apps, Default apps, then try again.";

   /// <summary>The whole report of a browser Windows could not start for want of the file (error 2).</summary>
   internal const string WindowsReport = "The default browser could not be opened (Windows error 2). " + Remedy;

   /// <summary>The whole report of a launcher that failed in some other way.</summary>
   internal const string OtherReport = "The default browser could not be opened (InvalidOperationException). " + Remedy;



   /// <summary>A provider's authorization address as the session client builds one, with the marked state, nonce and code challenge.</summary>
   internal static Uri SignInAddress { get; } = new("https://id.example.com/authorize?client_id=synthetic-client&response_type=code&redirect_uri=io.qrun.qqq-print-agent%3A%2Foauth%2Fcallback&scope=openid&state=" + State
      + "&nonce=" + Nonce + "&prompt=login&max_age=0&code_challenge_method=S256&code_challenge=" + Challenge);



   /// <summary>A provider's end-session address as the service returns one, with the marked ID token hint and state.</summary>
   internal static Uri SignOutAddress { get; } = new("https://id.example.com/end-session?id_token_hint=" + Hint + "&post_logout_redirect_uri=io.qrun.qqq-print-agent%3A%2Foauth%2Fcallback&state=" + State);



   /// <summary>The failure .NET throws when Windows cannot start what it was asked to open: error 2, with the text .NET gives it, which names the address in full.</summary>
   internal static Win32Exception AsWindows(Uri address)
      => new(2, "An error occurred trying to start process '" + address.AbsoluteUri + "' with working directory 'C:/Program Files/Synthetic'. The system cannot find the file specified.");



   /// <summary>A launcher failure of another kind whose text also names the address.</summary>
   internal static InvalidOperationException AsAnotherKind(Uri address) => new("The launcher could not open " + address.AbsoluteUri + ".");



   /// <summary>True when a text holds anything of either address beyond the provider's host: one of the marked values, a query, a parameter name or the path.</summary>
   internal static bool Leaks(string? text)
      => text is not null && new[] { State, Nonce, Challenge, Hint, "?", "client_id", "id_token_hint", "redirect_uri", "/authorize", "/end-session" }.Any(part => text.Contains(part, StringComparison.OrdinalIgnoreCase));



   /// <summary>Every public property of a view model as the text a view would show for it, which is what a binding can reach.</summary>
   internal static List<string?> Bound(object viewModel)
      => [.. viewModel.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(property => Convert.ToString(property.GetValue(viewModel), CultureInfo.InvariantCulture))];
}
