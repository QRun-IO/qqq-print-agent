/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// What the agent knows about operator control: whether the server has sent its state, the desired state, and the last contact
/// and command outcome. It lets the UI explain why dispatch is paused. A state file written by an older release may carry
/// members this record no longer has; they are ignored when it is read.
/// </summary>
public sealed record AgentControlState(string Support, DeviceDesiredState DesiredState,
   string? LastContact = null, string? LastCommand = null, string? LastOutcome = null, string? LastCommandAt = null)
{
   /// <summary>The state before any server contact: support unknown and dispatch running, so the agent never assumes it has been paused or authorized.</summary>
   public static AgentControlState Initial => new("UNKNOWN", new DeviceDesiredState(0, false, "CONNECTED"));



   /// <summary>
   /// Obsolete and always false. The polling prerelease saved a demand for authorization here when its HTTP control poll was
   /// refused. Nothing in the realtime service sets it, a saved value is not read back, and a blocked authorization is
   /// reported through the authentication status instead. The member remains only because the companion's footer still reads it.
   /// </summary>
   public bool AuthorizationRequired { get; }
}
