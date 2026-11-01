/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;

namespace Qqq.PrintAgent.Windows;

/// <summary>Protected service state. ToString never includes the session secret.</summary>
public sealed class AgentAuthentication(QqqSessionBinding binding, QqqSessionCredential? credential)
{
   public QqqSessionBinding Binding { get; } = binding;
   public QqqSessionCredential? Credential { get; } = credential;



   /// <summary>Returns a fixed label, so logging this state can never reveal the session secret.</summary>
   public override string ToString() => "Protected QQQ authorization state";
}

