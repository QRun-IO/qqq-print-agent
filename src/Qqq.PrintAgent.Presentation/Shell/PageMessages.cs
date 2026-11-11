/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.ComponentModel;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The window's message bar, with every message kept under the page that caused it (ADR-0005, "One status at a time,
/// per place"). The bar shows a message of the page on screen and no other: a message caused by another page never
/// appears here, so the wizard never shows what a server's page said and the other way round.
/// <para>
/// One rule holds for all of it: a message that has not been on screen is never replaced. A message has been on
/// screen once its page was the one on screen and it was the message the bar showed (<see cref="Current"/>). A page
/// therefore keeps its messages in a short list, in the order they arrived. A command can end after the operator has
/// left the page it was started from, and several can: each result waits under its page, and when the operator opens
/// the page the bar shows the oldest; <see cref="Dismiss"/> shows the next. Leaving a page keeps the messages that
/// were not on screen, whoever made the move. The message that was on screen is dropped when the operator made the
/// move, so coming back does not show an old result. When the shell made the move (it lands, follows a server the
/// wizard added, shows a neighbour, is told that the wizard ended), the operator did not choose to leave and may
/// not have read what was there: an error then stays under its page and is shown when the operator next opens
/// that page, as a failure that has waited. Anything less than an error is dropped then too, because a refusal
/// such as "Cannot start now" would otherwise be shown again when it is no longer true.
/// </para>
/// <para>
/// What arrives for the page on screen takes the place of the message the bar shows, which the operator has had in
/// front of them, with one exception: a failure that had been waiting stays until the operator dismisses it or
/// leaves, and what arrives meanwhile waits behind it. So a failure that came on screen a moment ago, because the
/// operator or the shell has just opened its page, is never covered by the next message.
/// </para>
/// <para>
/// A page keeps at most <see cref="MaximumPerPage"/> messages. When one more arrives, a failure takes the place of the
/// oldest waiting message that is not a failure; a message that is not a failure, and a failure when every waiting
/// message is one, is not kept. Whenever that happened the page says so itself, in a message of its own that counts
/// the results that were not kept and how many of them were failures, so a failure is never dropped without a
/// word. That message stands where the first result was lost. A failure is a message whose severity is a warning
/// or an error; the message on screen is never the one that gives way to make room.
/// </para>
/// <para>
/// When a server has gone its pages cannot be opened again. The failures still waiting under them are moved to the
/// page the shell shows instead, each saying which server it was about, and so is an error that was on screen on
/// one of them; what was waiting there and was not a failure is dropped, and so is a message on screen that was not
/// an error. A failure reported later for a server that has gone is shown on the page on screen in the same way.
/// A page posts here what is worth reading later: everything posted for a page that is not on screen waits, in
/// order. Which page is on screen is the shell's to say; it is used from the window's thread only.
/// </para>
/// </summary>
public sealed class PageMessages : ObservableObject
{
   /// <summary>The most messages a page keeps, the one on screen included. The note of what was not kept is not counted.</summary>
   public const int MaximumPerPage = 8;

   /// <summary>How many servers that went are remembered, so that a failure reported for one of them later can say which server it was about. The oldest is forgotten first.</summary>
   private const int MaximumServersRemembered = 32;

   /// <summary>The messages of each page that has any, oldest first. The first one of the page on screen is the one the bar shows.</summary>
   private readonly Dictionary<PageKey, List<Entry>> _messages = [];

   /// <summary>The servers that went, oldest first: each one's identifier and the name it had.</summary>
   private readonly List<KeyValuePair<string, string>> _went = [];

   /// <summary>Backs <see cref="Page"/>.</summary>
   private PageKey _page;

   /// <summary>Backs <see cref="Current"/>.</summary>
   private FeedbackMessage? _current;

   /// <summary>Backs <see cref="Waiting"/>.</summary>
   private int _waiting;



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



   /// <summary>The message the bar shows: the oldest one of the page on screen, or null when that page has none.</summary>
   public FeedbackMessage? Current
   {
      get => _current;
      private set => SetProperty(ref _current, value);
   }



   /// <summary>How many more messages of the page on screen wait behind <see cref="Current"/>; <see cref="Dismiss"/> shows the next of them.</summary>
   public int Waiting
   {
      get => _waiting;
      private set => SetProperty(ref _waiting, value);
   }



   /// <summary>
   /// Keeps a message under the page that caused it. For the page on screen it is shown at once, in place of the
   /// message the bar shows, unless that one is a failure that had been waiting, behind which it waits. For another
   /// page it waits, after those that already wait there, until the operator opens that page. For a page of a server
   /// that has gone, a failure is shown on the page on screen, saying which server it was about, and anything else
   /// is dropped.
   /// </summary>
   public void Show(PageKey cause, FeedbackMessage message)
   {
      ArgumentNullException.ThrowIfNull(cause);
      ArgumentNullException.ThrowIfNull(message);
      if(NameOfAServerThatWent(cause.ServerId) is not { } name)
      {
         Add(cause, message, carried: false);
      }
      else if(IsFailure(message))
      {
         Add(Page, ShellMessages.AboutAServerThatWent(message, name), carried: true);
      }
      Follow();
   }



   /// <summary>Removes the message the bar shows, as when the operator closes the bar, and shows the next one that waits for the page on screen, if any.</summary>
   public void Dismiss()
   {
      DropTheOneOnScreen();
      Follow();
   }



   /// <summary>
   /// Another page is on screen now. Those messages of the page that was left which had not been on screen stay
   /// under it. The message that was on screen is dropped when the move was made <paramref name="byTheOperator"/>.
   /// When the shell made the move it is dropped too, unless it is an error: an error stays under its page, as a
   /// failure that has waited, and is shown when the operator next opens that page. The bar shows the oldest
   /// message that waits for the page that was come to. Going to the page that is already on screen changes
   /// nothing.
   /// </summary>
   internal void MoveTo(PageKey page, bool byTheOperator)
   {
      ArgumentNullException.ThrowIfNull(page);
      if(page == Page)
      {
         return;
      }
      if(byTheOperator || !KeepTheErrorOnScreen())
      {
         DropTheOneOnScreen();
      }
      Page = page;
      Follow();
   }



   /// <summary>
   /// Takes what a snapshot whose list of servers can be relied on says: which servers are listed, and which ones
   /// went with it (identifier and name). A server that went is remembered until it is listed again. For every
   /// server that went, the failures waiting under its pages are moved under <paramref name="shown"/>, the page the
   /// shell shows now, each saying which server it was about. An error that was on screen on such a page is moved
   /// with them: the shell is taking the operator away from it, and its page cannot be opened again. Everything
   /// else of those pages is dropped: a message on screen that was not an error, and what was not a failure. When
   /// the page on screen was such a server's, the bar moves to <paramref name="shown"/>.
   /// </summary>
   internal void ServersWent(Func<string, bool> listed, IEnumerable<KeyValuePair<string, string>> went, PageKey shown)
   {
      _went.RemoveAll(server => listed(server.Key));
      foreach(KeyValuePair<string, string> server in went)
      {
         _went.RemoveAll(known => string.Equals(known.Key, server.Key, StringComparison.Ordinal));
         _went.Add(server);
      }
      _went.RemoveRange(0, Math.Max(0, _went.Count - MaximumServersRemembered));
      foreach(PageKey page in _messages.Keys.Where(page => NameOfAServerThatWent(page.ServerId) is not null).ToArray())
      {
         string name = NameOfAServerThatWent(page.ServerId)!;
         foreach(Entry entry in _messages[page].Where((entry, index) => index == 0 && page == Page ? IsError(entry.Message) : IsFailure(entry.Message)))
         {
            Add(shown, ShellMessages.AboutAServerThatWent(entry.Message, name), carried: true);
         }
         _messages.Remove(page);
      }
      if(NameOfAServerThatWent(Page.ServerId) is not null)
      {
         Page = shown;
      }
      Follow();
   }



   /// <summary>True for a message that reports a failure, or something the operator has to check: one that must not be dropped without a word.</summary>
   private static bool IsFailure(FeedbackMessage message) => message.Severity is FeedbackSeverity.Warning or FeedbackSeverity.Error;



   /// <summary>True for a message that reports an error: one that is not lost when the shell takes the operator away from it.</summary>
   private static bool IsError(FeedbackMessage message) => message.Severity == FeedbackSeverity.Error;



   /// <summary>The name a server had when it went, or null for a server that has not gone (and for a page that is not a server's).</summary>
   private string? NameOfAServerThatWent(string? serverId)
   {
      int at = _went.FindIndex(server => string.Equals(server.Key, serverId, StringComparison.Ordinal));
      return at < 0 ? null : _went[at].Value;
   }



   /// <summary>
   /// Puts a message at the end of a page's list. On the page on screen the message the bar shows gives way first,
   /// unless it is a failure that had been waiting. A message that is carried over from a server that went, one for
   /// a page that is not on screen and one that has to wait behind another count as having waited.
   /// </summary>
   private void Add(PageKey page, FeedbackMessage message, bool carried)
   {
      if(!_messages.TryGetValue(page, out List<Entry>? list))
      {
         _messages.Add(page, list = []);
      }
      bool onScreen = page == Page;
      if(onScreen && list.Count > 0 && !(list[0].Waited && IsFailure(list[0].Message)))
      {
         list.RemoveAt(0);
      }
      if(list.Count(entry => entry.NotKept == 0) < MaximumPerPage)
      {
         list.Add(new Entry(message, carried || !onScreen || list.Count > 0));
         return;
      }
      int gives = IsFailure(message) ? list.FindIndex(entry => entry.NotKept == 0 && !IsFailure(entry.Message)) : -1;
      if(gives >= 0)
      {
         list.RemoveAt(gives);
         list.Add(new Entry(message, Waited: true));
      }
      NoteNotKept(list, failure: gives < 0 && IsFailure(message));
   }



   /// <summary>Counts one more result that a page did not keep, in the page's own note of it, which is made when the first one is not kept and stays where it is from then on.</summary>
   private static void NoteNotKept(List<Entry> list, bool failure)
   {
      int at = list.FindIndex(entry => entry.NotKept > 0);
      int notKept = (at < 0 ? 0 : list[at].NotKept) + 1;
      int failures = (at < 0 ? 0 : list[at].FailuresNotKept) + (failure ? 1 : 0);
      Entry note = new(ShellMessages.ResultsNotKept(notKept, failures), Waited: true, notKept, failures);
      if(at < 0)
      {
         list.Add(note);
      }
      else
      {
         list[at] = note;
      }
   }



   /// <summary>When the bar shows an error, keeps it under its page as a message that has waited, so nothing that arrives covers it when its page is opened again, and says that it did. Anything else the bar shows, and nothing at all, is not kept.</summary>
   private bool KeepTheErrorOnScreen()
   {
      if(!_messages.TryGetValue(Page, out List<Entry>? list) || !IsError(list[0].Message))
      {
         return false;
      }
      list[0] = list[0] with { Waited = true };
      return true;
   }



   /// <summary>Drops the message the bar shows, which the operator has had in front of them, and forgets a page that has no message left.</summary>
   private void DropTheOneOnScreen()
   {
      if(_messages.TryGetValue(Page, out List<Entry>? list) && list.Count == 1)
      {
         _messages.Remove(Page);
      }
      else
      {
         list?.RemoveAt(0);
      }
   }



   /// <summary>Makes the bar follow the list of the page on screen. A page that is kept here has at least one message.</summary>
   private void Follow()
   {
      _messages.TryGetValue(Page, out List<Entry>? list);
      Current = list?[0].Message;
      Waiting = (list?.Count ?? 1) - 1;
   }



   /// <summary>One message of a page. <paramref name="Waited"/> is true when it was not shown the moment it arrived. <paramref name="NotKept"/> is above zero for the page's own note of the results it did not keep, of which <paramref name="FailuresNotKept"/> were failures.</summary>
   private sealed record Entry(FeedbackMessage Message, bool Waited, int NotKept = 0, int FailuresNotKept = 0);
}
