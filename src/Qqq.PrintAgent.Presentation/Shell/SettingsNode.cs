/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>The Settings node. Its identifier is the one the Settings item has had since 1.x.</summary>
public sealed class SettingsNode : TreeNode
{
   /// <summary>Makes the node.</summary>
   internal SettingsNode() : base(PageKey.Settings, "NavigateSettings", ShellMessages.NodeText(PageKind.Settings), "Settings", ShellMessages.NodeText(PageKind.Settings), level: 1)
   {
   }
}
