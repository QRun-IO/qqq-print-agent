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

   internal static string Rate(double value) => !double.IsFinite(value) || value < 0 ? "Unavailable" : Bytes(value) + "/s";

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
