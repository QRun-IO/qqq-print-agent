/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// A server's node: its name, its health as a badge (a level, a glyph and words), the sentence beside the name, the
/// service's own explanation of the state, how many of its jobs need review, the marker shown for as long as its
/// certificate exception is on, whether it is being removed, its three pages and the commands of its menu. The same
/// node stands for the server for as long as the server exists, so whether it is selected and whether it is expanded
/// stay with it while everything else follows the snapshots.
/// <para>
/// The name and the service's explanation are text the wire carried, so both are given the contract's printable
/// form here before anything shows them. A server that is being removed says so and offers no command.
/// </para>
/// </summary>
public sealed class ServerNode : TreeNode
{
   /// <summary>How many pages a server has beneath its node.</summary>
   internal const int PageCount = 3;

   /// <summary>Backs <see cref="Health"/>.</summary>
   private HealthBadge _health;

   /// <summary>Backs <see cref="StatusText"/>.</summary>
   private string _statusText;

   /// <summary>Backs <see cref="Detail"/>.</summary>
   private string _detail = string.Empty;

   /// <summary>Backs <see cref="AttentionCount"/>.</summary>
   private int _attentionCount;

   /// <summary>Backs <see cref="ExceptionMarker"/>.</summary>
   private string? _exceptionMarker;

   /// <summary>Backs <see cref="IsRemoving"/>.</summary>
   private bool _isRemoving;

   /// <summary>Backs <see cref="Commands"/>.</summary>
   private ServerCommands _commands = ServerCommands.None;



   /// <summary>Makes the node of a server that has just appeared, expanded, at its place in the list (counted from zero) among so many servers.</summary>
   internal ServerNode(ServerSnapshot server, ServerStatus status, int index, int count)
      : base(PageKey.Server(server.ServerId), Identifier(index), status.Name, status.Health.GlyphName, status.Name, level: 1)
   {
      ServerId = server.ServerId;
      _health = status.Health;
      _statusText = status.Sentence;
      IsExpanded = true;
      Children =
      [
         new ServerPageNode(ServerId, PageKind.Printers, status.Name, 1),
         new ServerPageNode(ServerId, PageKind.Jobs, status.Name, 2),
         new ServerPageNode(ServerId, PageKind.Connection, status.Name, 3),
      ];
      Update(server, status, index, count);
   }



   /// <summary>The server's identifier, which never changes.</summary>
   public string ServerId { get; }



   /// <summary>The server's name, printable. It is the node's label.</summary>
   public string Name => Text;



   /// <summary>The server's health: its level, a glyph and the level in words.</summary>
   public HealthBadge Health
   {
      get => _health;
      private set => SetProperty(ref _health, value);
   }



   /// <summary>The sentence beside the name (<see cref="ServerStatus.Sentence"/>).</summary>
   public string StatusText
   {
      get => _statusText;
      private set => SetProperty(ref _statusText, value);
   }



   /// <summary>The service's own explanation of the server's state, printable and at most <see cref="ManagementProtocol.MaximumMessageCharacters"/> UTF-16 units; empty when it gave none.</summary>
   public string Detail
   {
      get => _detail;
      private set => SetProperty(ref _detail, value);
   }



   /// <summary>How many of the server's jobs failed and have not been reviewed. Never negative.</summary>
   public int AttentionCount
   {
      get => _attentionCount;
      private set => SetProperty(ref _attentionCount, value);
   }



   /// <summary>The words of the marker shown while the server's development-only certificate exception is on; null while it is off.</summary>
   public string? ExceptionMarker
   {
      get => _exceptionMarker;
      private set => SetProperty(ref _exceptionMarker, value);
   }



   /// <summary>True while the server is being removed. It is still in the tree, says so, and offers no command.</summary>
   public bool IsRemoving
   {
      get => _isRemoving;
      private set => SetProperty(ref _isRemoving, value);
   }



   /// <summary>The server's three pages: Printers, Jobs, Connection.</summary>
   public IReadOnlyList<ServerPageNode> Children { get; }



   /// <summary>The commands of the server's menu.</summary>
   public ServerCommands Commands
   {
      get => _commands;
      private set => SetProperty(ref _commands, value);
   }



   /// <summary>The identifier of the server at a place in the list, counted from zero: <c>Server.1</c> for the first.</summary>
   internal static string Identifier(int index) => string.Create(CultureInfo.InvariantCulture, $"Server.{index + 1}");



   /// <summary>Takes the server's state from a snapshot, and its place in the list among so many servers. Nothing is raised for what did not change.</summary>
   internal void Update(ServerSnapshot server, ServerStatus status, int index, int count)
   {
      AutomationId = Identifier(index);
      Text = status.Name;
      Health = status.Health;
      GlyphName = status.Health.GlyphName;
      StatusText = status.Sentence;
      Detail = ManagementText.Bounded(server.Status, ManagementProtocol.MaximumMessageCharacters).Trim();
      AttentionCount = Math.Max(0, server.AttentionCount);
      ExceptionMarker = server.CertificateException.Enabled ? ShellMessages.ExceptionMarker : null;
      IsRemoving = server.Removing;
      AccessibleName = ShellMessages.ServerNodeName(status.Name, status.Health, server.CertificateException.Enabled, server.Removing);
      var commands = ServerCommands.For(server, index, count);
      if(!commands.Same(Commands))
      {
         Commands = commands;
      }
      foreach(ServerPageNode page in Children)
      {
         page.Rename(status.Name);
      }
   }
}
