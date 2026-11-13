/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>
/// The sentences a person reads when the server asks them to sign in or refuses them. Each says what happened and what
/// to do next. The application's name is used when this build knows one (a client edition names its server); otherwise
/// the sentence uses neutral words and names no product.
/// </summary>
public static class PermissionMessages
{
   /// <summary>
   /// What the person was doing when the server answered 403 Forbidden. The server gives one answer for every refusal,
   /// so the request that was refused is the only thing that tells these apart.
   /// </summary>
   public enum Refusal
   {
      /// <summary>A request that carried only the signed-in session: the account itself may not use printing.</summary>
      Account,

      /// <summary>Enrolling this computer: the account may not set up a print station (or not for the chosen place).</summary>
      StationSetup,

      /// <summary>A request that carried this print station's proof: the account or the print station lost its access.</summary>
      Station,
   }



   /// <summary>The server no longer accepts the session, or there is none.</summary>
   public static string SignInRequired(string? serverName) => Named(serverName) is { } name ? $"Sign in to {name} to continue." : "Sign in to continue.";



   /// <summary>The server accepted who the person is and refused what they asked for; signing in again will not help.</summary>
   public static string Denied(Refusal refusal, string? serverName)
   {
      string signedIn = Named(serverName) is { } name ? $"You are signed in to {name}" : "You are signed in";
      return refusal switch
      {
         Refusal.StationSetup => $"{signedIn}, but your account is not allowed to set up this print station. Ask an administrator to give your account permission to set up print stations, or to set this one up for you.",
         Refusal.Station => $"{signedIn}, but your account or this print station is no longer allowed to print. Ask an administrator to check your account's printing access and this print station's access.",
         _ => $"{signedIn}, but your account is not allowed to use printing. Ask an administrator to give your account access to printing.",
      };
   }



   /// <summary>A usable name, or null when the build knows none.</summary>
   private static string? Named(string? serverName) => string.IsNullOrWhiteSpace(serverName) ? null : serverName.Trim();
}
