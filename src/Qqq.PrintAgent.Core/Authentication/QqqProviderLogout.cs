/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The identity provider's own sign-out, ready for the host to open in the browser. The address carries the provider's ID token
/// hint, so it is held only in memory, handed straight to the browser, never logged, and its label is fixed text.
/// </summary>
public sealed class QqqProviderLogout
{
   /// <summary>Records the address to open, the registered address the provider returns the browser to, and the random state that binds the return to this sign-out.</summary>
   public QqqProviderLogout(Uri endSessionUri, Uri redirectUri, string state)
   {
      EndSessionUri = endSessionUri ?? throw new ArgumentNullException(nameof(endSessionUri));
      RedirectUri = redirectUri ?? throw new ArgumentNullException(nameof(redirectUri));
      State = state ?? throw new ArgumentNullException(nameof(state));
   }



   /// <summary>The provider's end-session address including the sensitive ID token hint. Only open it in the browser.</summary>
   public Uri EndSessionUri { get; }

   /// <summary>The registered callback the provider sends the browser to when the sign-out is finished.</summary>
   public Uri RedirectUri { get; }

   /// <summary>The random state this sign-out sent and expects back.</summary>
   public string State { get; }



   /// <summary>True only for a callback on exactly the registered address, with no fragment or user information and this sign-out's own state, so a forged or stale return cannot be taken as the provider finishing.</summary>
   public bool Matches(Uri callback)
   {
      ArgumentNullException.ThrowIfNull(callback);
      if(!callback.IsAbsoluteUri || callback.Fragment.Length != 0 || callback.UserInfo.Length != 0
         || !string.Equals(callback.GetLeftPart(UriPartial.Path), RedirectUri.AbsoluteUri, StringComparison.Ordinal) || callback.Query.Length < 2)
      { return false; }
      string? state = null;
      foreach(string entry in callback.Query[1..].Split('&'))
      {
         string[] pair = entry.Split('=', 2);
         if(pair.Length != 2)
         { return false; }
         if(string.Equals(Uri.UnescapeDataString(pair[0].Replace('+', ' ')), "state", StringComparison.Ordinal))
         {
            if(state is not null)
            { return false; }
            state = Uri.UnescapeDataString(pair[1].Replace('+', ' '));
         }
      }
      return string.Equals(state, State, StringComparison.Ordinal);
   }



   /// <summary>Returns a fixed redacted label, so logging or displaying a sign-out can never reveal the ID token hint or state.</summary>
   public override string ToString() => "QQQ provider sign-out (redacted)";
}
