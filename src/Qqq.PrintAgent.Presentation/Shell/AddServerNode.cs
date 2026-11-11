/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The Add server node, which shows the connection wizard. It is disabled, with the reason, while a wizard cannot
/// start: the service has not answered or its stored data is not ready, or the computer already has as many servers
/// as it may hold, counting one that is being removed. While a wizard is open the node stays enabled whatever else is
/// true, so the operator can always go back to it, and it says that one is in progress.
/// </summary>
public sealed class AddServerNode : TreeNode
{
   /// <summary>Backs <see cref="IsEnabled"/>.</summary>
   private bool _isEnabled;

   /// <summary>Backs <see cref="DisabledReason"/>.</summary>
   private string? _disabledReason = ServiceAvailability.Checking.Summary;

   /// <summary>Backs <see cref="InProgress"/>.</summary>
   private bool _inProgress;



   /// <summary>Makes the node, disabled until the service has answered.</summary>
   internal AddServerNode() : base(PageKey.Wizard, "AddServer", ShellMessages.NodeText(PageKind.Wizard), "AddServer", ShellMessages.NodeText(PageKind.Wizard), level: 1)
   {
   }



   /// <summary>True when choosing the node does something: a wizard can start, or one is open.</summary>
   public bool IsEnabled
   {
      get => _isEnabled;
      private set => SetProperty(ref _isEnabled, value);
   }



   /// <summary>Why the node is disabled and what to do, or null while it is enabled.</summary>
   public string? DisabledReason
   {
      get => _disabledReason;
      private set => SetProperty(ref _disabledReason, value);
   }



   /// <summary>True while a wizard is open, on screen or not.</summary>
   public bool InProgress
   {
      get => _inProgress;
      private set => SetProperty(ref _inProgress, value);
   }



   /// <summary>Takes the reason a wizard cannot start now (null when one can) and whether one is open.</summary>
   internal void Set(string? reasonNotToStart, bool wizardOpen)
   {
      IsEnabled = wizardOpen || reasonNotToStart is null;
      DisabledReason = IsEnabled ? null : reasonNotToStart;
      InProgress = wizardOpen;
      AccessibleName = wizardOpen ? ShellMessages.AddServerInProgress : ShellMessages.AddServer;
   }
}
