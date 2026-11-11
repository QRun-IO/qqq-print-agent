/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.ComponentModel;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// One node of the window's tree. Every node says which page selecting it shows, carries the identifier the UI tests
/// find it by, a label, the name of its glyph and the name a screen reader calls it, and knows where it stands: its
/// level (1 for a node of the tree itself, 2 for a server's page), its position among its siblings counted from one
/// and how many siblings there are. The tree keeps those three right after every change, so the view hands them to
/// the automation tree as they are. A node tells its bindings about every change on the thread that made it.
/// </summary>
public abstract class TreeNode : ObservableObject
{
   /// <summary>Backs <see cref="AutomationId"/>.</summary>
   private string _automationId;

   /// <summary>Backs <see cref="Text"/>.</summary>
   private string _text;

   /// <summary>Backs <see cref="GlyphName"/>.</summary>
   private string _glyphName;

   /// <summary>Backs <see cref="AccessibleName"/>.</summary>
   private string _accessibleName;

   /// <summary>Backs <see cref="PositionInSet"/>.</summary>
   private int _positionInSet;

   /// <summary>Backs <see cref="SizeOfSet"/>.</summary>
   private int _sizeOfSet;

   /// <summary>Backs <see cref="IsSelected"/>.</summary>
   private bool _isSelected;

   /// <summary>Backs <see cref="IsExpanded"/>.</summary>
   private bool _isExpanded;

   /// <summary>Backs <see cref="IsBusy"/>.</summary>
   private bool _isBusy;



   /// <summary>Records what the node is. Only the node types of this assembly derive from it.</summary>
   private protected TreeNode(PageKey key, string automationId, string text, string glyphName, string accessibleName, int level)
   {
      Key = key;
      _automationId = automationId;
      _text = text;
      _glyphName = glyphName;
      _accessibleName = accessibleName;
      Level = level;
   }



   /// <summary>The page that selecting this node shows.</summary>
   public PageKey Key { get; }



   /// <summary>The identifier the UI tests find the node by. A server's is <c>Server.&lt;n&gt;</c>, its place in the tree counted from one; every other node's is fixed.</summary>
   public string AutomationId
   {
      get => _automationId;
      private protected set => SetProperty(ref _automationId, value);
   }



   /// <summary>The node's label.</summary>
   public string Text
   {
      get => _text;
      private protected set => SetProperty(ref _text, value);
   }



   /// <summary>The name of the glyph drawn beside the label. A server's is its health's glyph, so its state is a shape as well as a colour.</summary>
   public string GlyphName
   {
      get => _glyphName;
      private protected set => SetProperty(ref _glyphName, value);
   }



   /// <summary>What a screen reader calls the node. It is never empty.</summary>
   public string AccessibleName
   {
      get => _accessibleName;
      private protected set => SetProperty(ref _accessibleName, value);
   }



   /// <summary>1 for a node of the tree itself, 2 for one of a server's pages.</summary>
   public int Level { get; }



   /// <summary>The node's place among its siblings, counted from one.</summary>
   public int PositionInSet
   {
      get => _positionInSet;
      private set => SetProperty(ref _positionInSet, value);
   }



   /// <summary>How many siblings the node has, itself included.</summary>
   public int SizeOfSet
   {
      get => _sizeOfSet;
      private set => SetProperty(ref _sizeOfSet, value);
   }



   /// <summary>True for the one node whose page is on screen. The tree sets it.</summary>
   public bool IsSelected
   {
      get => _isSelected;
      internal set => SetProperty(ref _isSelected, value);
   }



   /// <summary>True while a server's node shows its three pages. The operator changes it, and it stays as they left it for as long as the server exists.</summary>
   public bool IsExpanded
   {
      get => _isExpanded;
      set => SetProperty(ref _isExpanded, value);
   }



   /// <summary>True while a command runs on what the node stands for: a server, the computer or the wizard.</summary>
   public bool IsBusy
   {
      get => _isBusy;
      internal set => SetProperty(ref _isBusy, value);
   }



   /// <summary>Records where the node stands among its siblings.</summary>
   internal void Place(int positionInSet, int sizeOfSet)
   {
      PositionInSet = positionInSet;
      SizeOfSet = sizeOfSet;
   }
}
