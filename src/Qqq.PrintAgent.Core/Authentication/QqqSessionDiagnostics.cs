/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// Credential-free progress reporting for the session client. Info entries describe each step of discovery, sign-in,
/// session restore, identity checks and the realtime upgrade so an operator can see where a new server or account
/// stops working; debug entries add request-level detail (route, status, timing, header names) and are produced only
/// while the host says debug logging is on. Callers never receive tokens, cookies, passwords, codes or device proof.
/// </summary>
public sealed class QqqSessionDiagnostics(Action<string>? info = null, Action<string>? debug = null, Func<bool>? debugEnabled = null)
{
   /// <summary>A sink that discards everything, for callers that have no log.</summary>
   public static QqqSessionDiagnostics None { get; } = new();



   /// <summary>True when a debug sink exists and the host currently wants debug detail.</summary>
   public bool DebugEnabled => debug is not null && (debugEnabled?.Invoke() ?? true);



   /// <summary>Records one step of normal operation.</summary>
   public void Info(string message) => info?.Invoke(message);



   /// <summary>Records request-level detail when debug logging is on; the message is built only in that case.</summary>
   public void Debug(Func<string> message)
   {
      if(DebugEnabled)
      { debug!.Invoke(message()); }
   }



   /// <summary>
   /// Describes an address without its query, fragment or user information, so a logged route can never carry a code,
   /// state or credential. The host is in the form every host is shown in (see <see cref="QqqOrigin.ShownAuthority"/>):
   /// the ASCII name the connection is made to, because the address may be one a server wrote (its sign-in provider's
   /// discovery document or endpoints), and a look-alike letter must read as the "xn--" name it is.
   /// </summary>
   public static string Describe(Uri uri) => uri.IsAbsoluteUri ? uri.Scheme + "://" + QqqOrigin.ShownAuthority(uri) + uri.AbsolutePath : uri.ToString();



   /// <summary>Describes a failure by type and message, followed by each inner cause the same way, so "see inner exception" never hides the real reason; stack traces belong to the host's error sink.</summary>
   public static string Describe(Exception exception)
   {
      ArgumentNullException.ThrowIfNull(exception);
      var parts = new List<string>();
      for(Exception? current = exception; current is not null && parts.Count < 4; current = current.InnerException)
      { parts.Add(current.GetType().Name + ": " + current.Message); }
      return string.Join(" <- ", parts);
   }



   /// <summary>The human-readable reason for a failure: the message and each inner cause, without type names, for an on-screen explanation.</summary>
   public static string Explain(Exception exception)
   {
      ArgumentNullException.ThrowIfNull(exception);
      var parts = new List<string>();
      for(Exception? current = exception; current is not null && parts.Count < 4; current = current.InnerException)
      {
         string message = current.Message.Trim();
         if(message.Length != 0 && !parts.Contains(message, StringComparer.Ordinal))
         { parts.Add(message); }
      }
      return string.Join(" ", parts);
   }
}
