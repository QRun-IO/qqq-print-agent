/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>
/// How a run of the wizard ended, which tells the window what to show in its place: nothing in particular after a
/// cancel, the Computer page when the service's stored data is not ready, or one server's pages. The wizard sets it
/// once and does nothing afterwards; the window then disposes the wizard.
/// </summary>
public sealed record WizardEnd
{
   /// <summary>Records where the window goes.</summary>
   private WizardEnd(string? serverId, bool computerPage)
   {
      ServerId = serverId;
      OpensComputerPage = computerPage;
   }



   /// <summary>The operator cancelled. Nothing the wizard did is left behind.</summary>
   public static WizardEnd Cancelled { get; } = new(null, computerPage: false);



   /// <summary>The service is not running connections, so the wizard cannot go on; the Computer page says why.</summary>
   public static WizardEnd ComputerPage { get; } = new(null, computerPage: true);



   /// <summary>The server to open; null unless the wizard ended on a server.</summary>
   public string? ServerId { get; }



   /// <summary>True when the window is to show the Computer page.</summary>
   public bool OpensComputerPage { get; }



   /// <summary>The wizard ended on the server with this identifier, which the window opens.</summary>
   public static WizardEnd Server(string serverId)
      => ManagementProtocol.IsServerId(serverId) ? new WizardEnd(serverId, computerPage: false) : throw new ArgumentException("A server is opened by its full identifier.", nameof(serverId));
}
