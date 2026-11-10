/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>The result of <see cref="ManagementOperations.PairingEnroll"/>: the identifier of the device this computer now is on the server. The device's token stays in the service's protected storage.</summary>
public sealed record EnrollResult(string DeviceId)
{
   /// <summary>Takes the device identifier from the server's enrollment answer and nothing else; the answer also holds the device token.</summary>
   public static EnrollResult From(EnrollmentResponse response)
   {
      ArgumentNullException.ThrowIfNull(response);
      return new EnrollResult(response.DeviceId);
   }
}
