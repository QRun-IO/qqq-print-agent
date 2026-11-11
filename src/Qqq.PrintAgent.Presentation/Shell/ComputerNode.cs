/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>The Computer node: this computer's details, the Windows service and companion startup. It is also where the window says why the service is not available.</summary>
public sealed class ComputerNode : TreeNode
{
   /// <summary>Makes the node.</summary>
   internal ComputerNode() : base(PageKey.Computer, "NavigateComputer", ShellMessages.NodeText(PageKind.Computer), "Computer", ShellMessages.NodeText(PageKind.Computer), level: 1)
   {
   }
}
