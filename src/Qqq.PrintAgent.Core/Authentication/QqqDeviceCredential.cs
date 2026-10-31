/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>Secondary device possession proof. It never replaces the current QQQ user session.</summary>
public sealed class QqqDeviceCredential
{
   public QqqDeviceCredential(string deviceId, string deviceToken)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
      ArgumentException.ThrowIfNullOrWhiteSpace(deviceToken);
      if(deviceId.Length > 128 || deviceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
      {
         throw new ArgumentException("Invalid device credential.", nameof(deviceId));
      }
      if(deviceToken.Length > 4096 || deviceToken.Any(character => character < 33 || character > 126))
      {
         throw new ArgumentException("Invalid device credential.", nameof(deviceToken));
      }
      DeviceId = deviceId;
      DeviceToken = deviceToken;
   }

   public string DeviceId { get; }
   public string DeviceToken { get; }
   public override string ToString() => "QQQ device credential (redacted)";
}
