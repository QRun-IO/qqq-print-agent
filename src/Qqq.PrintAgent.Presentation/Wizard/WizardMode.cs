/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>
/// What a run of the connection wizard is for, and which server it is about: adding a new server, which is about no
/// server yet, or repairing or pairing one that exists, named by its identifier as the snapshot gives it. A mode can
/// only be made in one of these three ways, so a repair or a pairing always names a server and adding never does.
/// </summary>
public sealed record WizardMode
{
   /// <summary>Records the kind and the server.</summary>
   private WizardMode(WizardModeKind kind, string? serverId)
   {
      Kind = kind;
      ServerId = serverId;
   }



   /// <summary>Adding a new server.</summary>
   public static WizardMode AddServer { get; } = new(WizardModeKind.AddServer, null);



   /// <summary>What the run is for.</summary>
   public WizardModeKind Kind { get; }



   /// <summary>The server the run is about; null when a server is being added.</summary>
   public string? ServerId { get; }



   /// <summary>Signing in again to the server with this identifier.</summary>
   public static WizardMode Repair(string serverId) => new(WizardModeKind.Repair, Existing(serverId));



   /// <summary>Pairing this computer with the server with this identifier.</summary>
   public static WizardMode Pair(string serverId) => new(WizardModeKind.Pair, Existing(serverId));



   /// <summary>Returns a server identifier in the one form the contract writes it, and refuses any other text. The refusal never repeats what was given.</summary>
   private static string Existing(string serverId)
      => ManagementProtocol.IsServerId(serverId) ? serverId : throw new ArgumentException("A repair or a pairing names a server by its full identifier.", nameof(serverId));
}
