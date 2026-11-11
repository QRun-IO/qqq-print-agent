/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.ObjectModel;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The window's tree: one node for every server, in the order of the snapshot's list, each with its Printers, Jobs
/// and Connection pages; then Add server, Computer, Logs and Settings. Each snapshot is reconciled into it by the
/// servers' identifiers: a server that is still there keeps its node, so whether it is selected and whether it is
/// expanded survive; a server that is new gets a node; one that is gone loses its node; and the nodes are put in the
/// snapshot's order by moving them, never by emptying the list. When the server whose page is on screen is gone,
/// the selection moves to its neighbour: the server that came after it, or the one before it when it was last.
/// <para>
/// One node is selected at all times. After every reconcile each node's identifier, level, position among its
/// siblings and the size of that set are right: a server is <c>Server.&lt;n&gt;</c> by its place in the list counted
/// from one, so the identifiers are renumbered when the order changes or a server goes.
/// </para>
/// </summary>
public sealed class ServerTree
{
   /// <summary>The servers' nodes by identifier.</summary>
   private readonly Dictionary<string, ServerNode> _byId = new(StringComparer.Ordinal);

   /// <summary>The servers' nodes in the order of the last snapshot.</summary>
   private List<ServerNode> _servers = [];



   /// <summary>Makes the tree of a computer whose servers are not known yet, with the Computer page selected.</summary>
   internal ServerTree()
   {
      Nodes = [AddServer, Computer, Logs, Settings];
      Place();
      Computer.IsSelected = true;
   }



   /// <summary>The nodes of the tree itself, top to bottom: the servers, Add server, Computer, Logs, Settings.</summary>
   public ObservableCollection<TreeNode> Nodes { get; }



   /// <summary>The servers' nodes, in order.</summary>
   public IReadOnlyList<ServerNode> Servers => _servers;



   /// <summary>The Add server node.</summary>
   public AddServerNode AddServer { get; } = new();



   /// <summary>The Computer node.</summary>
   public ComputerNode Computer { get; } = new();



   /// <summary>The Logs node.</summary>
   public LogsNode Logs { get; } = new();



   /// <summary>The Settings node.</summary>
   public SettingsNode Settings { get; } = new();



   /// <summary>The page of the selected node.</summary>
   public PageKey Selected { get; private set; } = PageKey.Computer;



   /// <summary>A server's node by the server's identifier, or null when the tree has no such server.</summary>
   public ServerNode? Server(string? serverId) => serverId is not null && _byId.TryGetValue(serverId, out ServerNode? node) ? node : null;



   /// <summary>The node that shows a page, or null when the page belongs to a server the tree does not have.</summary>
   public TreeNode? Find(PageKey key)
   {
      ArgumentNullException.ThrowIfNull(key);
      return key.Kind switch
      {
         PageKind.Wizard => AddServer,
         PageKind.Computer => Computer,
         PageKind.Logs => Logs,
         PageKind.Settings => Settings,
         PageKind.Server => Server(key.ServerId),
         _ => Server(key.ServerId)?.Children.First(page => page.Key.Kind == key.Kind),
      };
   }



   /// <summary>
   /// Selects the node of a page, and says whether it could: a page of a server the tree does not have cannot be
   /// selected, and the selection then stays where it was. Selecting one of a server's pages expands the server, so
   /// the selected node is never hidden.
   /// </summary>
   internal bool Select(PageKey key)
   {
      if(Find(key) is not { } node)
      {
         return false;
      }
      if(Find(Selected) is { } previous)
      {
         previous.IsSelected = false;
      }
      node.IsSelected = true;
      Selected = key;
      if(node is ServerPageNode)
      {
         Server(key.ServerId)!.IsExpanded = true;
      }
      return true;
   }



   /// <summary>Shows on a node that a command runs under a gate's key: a server's identifier, the computer's key or the wizard's. It is called on the window's thread only; every reconcile reads the same from the gate again, for every node.</summary>
   internal void SetBusy(string key, bool busy)
   {
      TreeNode? node = key switch
      {
         OperationGate.Wizard => AddServer,
         OperationGate.Computer => Computer,
         _ => Server(key),
      };
      if(node is not null)
      {
         node.IsBusy = busy;
      }
   }



   /// <summary>
   /// Takes a snapshot's servers. <paramref name="current"/> is false when the service has stopped answering since
   /// the snapshot was read, and every server's health is then shown as unknown. <paramref name="busy"/> says whether
   /// a command runs under a gate's key now; it is asked for every server, for the wizard and for the computer, so
   /// a change of the gate that could not be shown when it happened is shown here. When the selected server is gone and it has no neighbour, the selection moves
   /// to <paramref name="whenNoServerIsLeft"/>. The node of the selected server that went is returned, so the caller
   /// can say that it went; null when the selection did not have to move.
   /// </summary>
   internal ServerNode? Reconcile(IReadOnlyList<ServerSnapshot> servers, IReadOnlyList<ComputerPrinter> printers, bool current, Func<string, bool> busy, PageKey whenNoServerIsLeft)
   {
      List<ServerNode> before = _servers;
      List<ServerNode> after = new(servers.Count);
      for(int index = 0; index < servers.Count; index++)
      {
         ServerSnapshot server = servers[index];
         var status = ServerStatus.From(server, printers, current);
         if(_byId.TryGetValue(server.ServerId, out ServerNode? node))
         {
            node.Update(server, status, index, servers.Count);
         }
         else
         {
            node = new ServerNode(server, status, index, servers.Count);
            _byId.Add(server.ServerId, node);
         }
         node.IsBusy = busy(server.ServerId);
         after.Add(node);
      }
      foreach(ServerNode gone in before.Except(after))
      {
         _byId.Remove(gone.ServerId);
      }
      _servers = after;
      AddServer.IsBusy = busy(OperationGate.Wizard);
      Computer.IsBusy = busy(OperationGate.Computer);
      Arrange([.. after, AddServer, Computer, Logs, Settings]);
      Place();
      return MoveSelectionFromAServerThatWent(before, whenNoServerIsLeft);
   }



   /// <summary>When the selected page belongs to a server that is no longer in the tree, selects its neighbour's overview, or the page given when no server is left, and returns the node that went.</summary>
   private ServerNode? MoveSelectionFromAServerThatWent(List<ServerNode> before, PageKey whenNoServerIsLeft)
   {
      if(Selected.ServerId is not { } selected || _byId.ContainsKey(selected))
      {
         return null;
      }
      int place = before.FindIndex(node => string.Equals(node.ServerId, selected, StringComparison.Ordinal));
      ServerNode went = before[place];
      ServerNode? neighbour = before.Skip(place + 1).FirstOrDefault(_servers.Contains) ?? before.Take(place).LastOrDefault(_servers.Contains);
      went.IsSelected = false;
      foreach(ServerPageNode page in went.Children)
      {
         page.IsSelected = false;
      }
      Select(neighbour?.Key ?? whenNoServerIsLeft);
      return went;
   }



   /// <summary>Puts the tree's nodes in the order given: removes those that went, then moves or inserts each of the others into its place. A list that is already right is not touched, so the view is told only about what changed.</summary>
   private void Arrange(List<TreeNode> order)
   {
      for(int index = Nodes.Count - 1; index >= 0; index--)
      {
         if(!order.Contains(Nodes[index]))
         {
            Nodes.RemoveAt(index);
         }
      }
      for(int index = 0; index < order.Count; index++)
      {
         int at = Nodes.IndexOf(order[index]);
         if(at < 0)
         {
            Nodes.Insert(index, order[index]);
         }
         else if(at != index)
         {
            Nodes.Move(at, index);
         }
      }
   }



   /// <summary>Tells every node of the tree itself its position and how many there are.</summary>
   private void Place()
   {
      for(int index = 0; index < Nodes.Count; index++)
      {
         Nodes[index].Place(index + 1, Nodes.Count);
      }
   }
}
