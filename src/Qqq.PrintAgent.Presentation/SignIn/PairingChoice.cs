/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>What the operator wants done with this computer's pairing when a session is handed to the service. Keeping the current pairing is the default; the other two are explicit choices because they change which device the server sees.</summary>
public enum PairingChoice
{
   /// <summary>Reuse the saved pairing; an existing binding is only verified.</summary>
   KeepCurrent,

   /// <summary>Associate an older, unbound device with the signed-in account.</summary>
   ClaimLegacyDevice,

   /// <summary>Set up a new pairing, for a different server or a revoked device.</summary>
   StartNewPairing,
}
