/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent;

/// <summary>Readable transport observations without exposing URL credentials or inventing missing measurements.</summary>
internal static class TransportFormatting
{
   private static readonly string[] Units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];



   /// <summary>Formats a byte count in binary units (B, KiB, MiB and so on) with one decimal, and says "Unavailable" for a negative or non-finite value instead of inventing a number.</summary>
   internal static string Bytes(double value)
   {
      if(!double.IsFinite(value) || value < 0)
      {
         return "Unavailable";
      }
      int unit = 0;
      while(value >= 1024 && unit < Units.Length - 1)
      {
         value /= 1024;
         unit++;
      }
      return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + Units[unit];
   }



   /// <summary>Formats a bytes-per-second rate, or "Unavailable" when there is no valid measurement.</summary>
   internal static string Rate(double value) => !double.IsFinite(value) || value < 0 ? "Unavailable" : Bytes(value) + "/s";



   /// <summary>Shows only the host and port of the server address, never credentials; an address with a user name, an unsupported scheme or no address at all becomes the generic "Connection settings" label.</summary>
   internal static string Server(string? address)
   {
      if(!Uri.TryCreate(address, UriKind.Absolute, out Uri? origin)
         || (!string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) && !string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
         || !string.IsNullOrEmpty(origin.UserInfo))
      {
         return "Connection settings";
      }
      return origin.Authority;
   }



   /// <summary>Words the WebSocket state as text such as "WebSocket connected (WSS)", so the state is readable without relying on a color, and says "unavailable" when the protocol is not a WebSocket one.</summary>
   internal static string State(string? protocol, string? state)
   {
      if(protocol is not ("WS" or "WSS"))
      {
         return "WebSocket unavailable";
      }
      string status = state switch
      {
         "CONNECTED" => "connected",
         "CONNECTING" => "connecting",
         "RECONNECTING" => "reconnecting",
         "AUTHORIZATION_REQUIRED" => "sign-in required",
         _ => "disconnected",
      };
      return $"WebSocket {status} ({protocol})";
   }
}
