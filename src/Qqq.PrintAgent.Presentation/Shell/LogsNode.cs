/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>The Logs node. Its identifier is the one the Logs item has had since 1.x.</summary>
public sealed class LogsNode : TreeNode
{
   /// <summary>Makes the node.</summary>
   internal LogsNode() : base(PageKey.Logs, "NavigateLogs", ShellMessages.NodeText(PageKind.Logs), "Logs", ShellMessages.NodeText(PageKind.Logs), level: 1)
   {
   }
}
