/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// One of the three pages beneath a server's node: Printers, Jobs or Connection. Its identifier is the same for
/// every server (<c>NavigatePrinters</c>, <c>NavigateJobs</c>, <c>NavigateConnection</c>) and carries nothing of the
/// server, so a UI test finds it through its server's node; the name a screen reader calls it says which server it
/// belongs to.
/// </summary>
public sealed class ServerPageNode : TreeNode
{
   /// <summary>Makes the node of one of a server's pages, at its place among the three.</summary>
   internal ServerPageNode(string serverId, PageKind kind, string serverName, int position)
      : base(PageKey.ForServer(kind, serverId), Identifier(kind), ShellMessages.NodeText(kind), kind.ToString(), ShellMessages.ServerPageNodeName(ShellMessages.NodeText(kind), serverName), level: 2)
   {
      Place(position, ServerNode.PageCount);
   }



   /// <summary>The identifier of a server's page in the tree. The first two are the ones the Printers and Jobs items have had since 1.x; they are fixed text and do not follow the labels.</summary>
   private static string Identifier(PageKind kind) => kind switch
   {
      PageKind.Printers => "NavigatePrinters",
      PageKind.Jobs => "NavigateJobs",
      _ => "NavigateConnection",
   };



   /// <summary>Follows the server's name, which is part of what a screen reader calls this node.</summary>
   internal void Rename(string serverName) => AccessibleName = ShellMessages.ServerPageNodeName(Text, serverName);
}
