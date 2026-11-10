/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.SignIn;

/// <summary>Reads the pairing box of the connection dialog. The box lists the choices in a fixed order, and anything unexpected (including no selection) means the safe default of keeping the current pairing.</summary>
public static class PairingSelection
{
   /// <summary>The pairing choice for the selected position: the second entry claims a legacy device, the third starts a new pairing, and every other position keeps the current pairing.</summary>
   public static PairingChoice FromIndex(int selectedIndex) => selectedIndex switch
   {
      1 => PairingChoice.ClaimLegacyDevice,
      2 => PairingChoice.StartNewPairing,
      _ => PairingChoice.KeepCurrent,
   };
}
