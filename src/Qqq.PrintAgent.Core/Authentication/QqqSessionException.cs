/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Authentication;

/// <summary>A sanitized failure classification; response bodies and credentials are never included.</summary>
public sealed class QqqSessionException(QqqSessionException.FailureKind failure, string message) : IOException(message)
{
   public enum FailureKind
   {
      ConfigurationRequired,
      InvalidResponse,
      AuthenticationRequired,
      PermissionDenied,
      TransportRefused,
   }

   public FailureKind Failure { get; } = failure;
}
