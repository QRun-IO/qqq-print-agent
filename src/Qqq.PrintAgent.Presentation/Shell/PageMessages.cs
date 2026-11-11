/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.ComponentModel;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The window's message bar, with every message kept under the page that caused it (ADR-0005, "One status at a time,
/// per place"). The bar shows the message of the page on screen and no other: a message caused by another page never
/// appears here, so the wizard never shows what a server's page said and the other way round. Leaving a page drops
/// its message, so coming back to it later does not show an old result.
/// <para>
/// A command can end after the operator has left the page it was started from. Its message is then kept under that
/// page, unseen, and is shown when the operator next opens the page, so a result is never lost and never shown in
/// the wrong place; it is dropped when they leave the page after that. A page has one message at a time: a newer one
/// replaces it. Which page is on screen is the shell's to say: only it moves the bar from one page to another.
/// </para>
/// </summary>
public sealed class PageMessages : ObservableObject
{
   /// <summary>The message of each page that has one.</summary>
   private readonly Dictionary<PageKey, FeedbackMessage> _messages = [];

   /// <summary>Backs <see cref="Page"/>.</summary>
   private PageKey _page;

   /// <summary>Backs <see cref="Current"/>.</summary>
   private FeedbackMessage? _current;



   /// <summary>Starts with the page that is on screen and no message.</summary>
   public PageMessages(PageKey page)
   {
      _page = page ?? throw new ArgumentNullException(nameof(page));
   }



   /// <summary>The page on screen.</summary>
   public PageKey Page
   {
      get => _page;
      private set => SetProperty(ref _page, value);
   }



   /// <summary>The message of the page on screen, or null when it has none.</summary>
   public FeedbackMessage? Current
   {
      get => _current;
      private set => SetProperty(ref _current, value);
   }



   /// <summary>Keeps a message under the page that caused it, in place of any that page had, and shows it when that page is the one on screen.</summary>
   public void Show(PageKey cause, FeedbackMessage message)
   {
      ArgumentNullException.ThrowIfNull(cause);
      ArgumentNullException.ThrowIfNull(message);
      _messages[cause] = message;
      if(cause == Page)
      {
         Current = message;
      }
   }



   /// <summary>Removes the message of the page on screen, as when the operator closes the bar.</summary>
   public void Dismiss()
   {
      _messages.Remove(Page);
      Current = null;
   }



   /// <summary>The operator went to another page: the message of the page they left is dropped, and the bar shows the message of the page they came to, if a command left one there meanwhile. Going to the page that is already on screen changes nothing.</summary>
   internal void MoveTo(PageKey page)
   {
      ArgumentNullException.ThrowIfNull(page);
      if(page == Page)
      {
         return;
      }
      _messages.Remove(Page);
      Page = page;
      Current = _messages.GetValueOrDefault(page);
   }



   /// <summary>Drops every message kept under a server's pages, when the server is gone and none of its pages can be opened again.</summary>
   internal void Forget(string serverId)
   {
      foreach(PageKey page in _messages.Keys.Where(page => string.Equals(page.ServerId, serverId, StringComparison.Ordinal)).ToArray())
      {
         _messages.Remove(page);
      }
      if(string.Equals(Page.ServerId, serverId, StringComparison.Ordinal))
      {
         Current = null;
      }
   }
}
