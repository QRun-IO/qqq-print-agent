/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>The message bar by page: a message is shown on the page that caused it and nowhere else, and is dropped when that page is left.</summary>
public sealed class PageMessagesTests
{
   /// <summary>A message for a test.</summary>
   private static FeedbackMessage Message(string text) => new("Title", text, FeedbackSeverity.Informational);



   /// <summary>A message shown on a page is there while the page is on screen, is gone when the operator goes to another page, and is not there when they come back.</summary>
   [Fact]
   public async Task Message_BelongsToItsPage_IsDroppedOnLeaving()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      var printers = PageKey.ForServer(PageKind.Printers, Snapshots.Id(1));
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(2));
      Assert.Equal(printers, fixture.Shell.Selected);
      fixture.Shell.Messages.Show(printers, Message("The printer is shared."));
      Assert.Equal("The printer is shared.", fixture.Shell.Messages.Current!.Message);

      fixture.Shell.Select(jobs);

      Assert.Null(fixture.Shell.Messages.Current);
      Assert.Equal(jobs, fixture.Shell.Messages.Page);

      fixture.Shell.Select(printers);

      Assert.Null(fixture.Shell.Messages.Current);

      fixture.Shell.Messages.Show(printers, Message("Shown again."));
      fixture.Shell.Select(printers);
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      Assert.Equal("Shown again.", fixture.Shell.Messages.Current!.Message);
   }



   /// <summary>
   /// A message caused by a page that is not on screen is not shown on the page that is. It is kept under its own
   /// page and shown when the operator opens that page, so the result of a command that ended after they left is not
   /// lost; it is dropped when they leave that page.
   /// </summary>
   [Fact]
   public void Message_ForAnotherPage_IsNotShownHere_AndWaitsOnItsOwnPage()
   {
      var connection = PageKey.ForServer(PageKind.Connection, Snapshots.Id(1));
      PageMessages messages = new(PageKey.Wizard);

      messages.Show(connection, Message("Signed out of Server 1."));

      Assert.Null(messages.Current);
      Assert.Equal(PageKey.Wizard, messages.Page);

      messages.MoveTo(PageKey.Logs);
      Assert.Null(messages.Current);

      messages.MoveTo(connection);
      Assert.Equal("Signed out of Server 1.", messages.Current!.Message);

      messages.MoveTo(PageKey.Logs);
      messages.MoveTo(connection);
      Assert.Null(messages.Current);
   }



   /// <summary>A page has one message: a newer one takes its place. Closing the bar removes it. Going to the page that is already on screen keeps it. The bar tells its bindings what changed.</summary>
   [Fact]
   public void Message_IsReplacedDismissedAndKept()
   {
      PageMessages messages = new(PageKey.Computer);
      List<string?> raised = [];
      messages.PropertyChanged += (_, change) => raised.Add(change.PropertyName);

      messages.Show(PageKey.Computer, Message("First."));
      messages.Show(PageKey.Computer, Message("Second."));
      messages.MoveTo(PageKey.Computer);

      Assert.Equal("Second.", messages.Current!.Message);
      Assert.Equal([nameof(PageMessages.Current), nameof(PageMessages.Current)], raised);

      messages.Dismiss();
      Assert.Null(messages.Current);
      messages.MoveTo(PageKey.Logs);
      messages.MoveTo(PageKey.Computer);
      Assert.Null(messages.Current);
      Assert.Equal([nameof(PageMessages.Current), nameof(PageMessages.Current), nameof(PageMessages.Current), nameof(PageMessages.Page), nameof(PageMessages.Page)], raised);
   }



   /// <summary>When a server is gone its pages cannot be opened again, so the messages kept under them are dropped, the one on screen included; other pages keep theirs.</summary>
   [Fact]
   public void Message_OfAServerThatIsGone_IsForgotten()
   {
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1));
      var connection = PageKey.ForServer(PageKind.Connection, Snapshots.Id(1));
      var other = PageKey.Server(Snapshots.Id(2));
      PageMessages messages = new(jobs);
      messages.Show(jobs, Message("On screen."));
      messages.Show(connection, Message("Waiting."));
      messages.Show(other, Message("Another server's."));

      messages.Forget(Snapshots.Id(1));

      Assert.Null(messages.Current);
      messages.MoveTo(connection);
      Assert.Null(messages.Current);
      messages.MoveTo(other);
      Assert.Equal("Another server's.", messages.Current!.Message);

      messages.Forget(Snapshots.Id(1));
      Assert.Equal("Another server's.", messages.Current!.Message);
   }



   /// <summary>A message kept under a server's page is forgotten when a snapshot no longer lists the server whose page was on screen.</summary>
   [Fact]
   public async Task Message_OfTheSelectedServerThatWent_IsNotCarriedToItsNeighbour()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      fixture.Shell.Messages.Show(fixture.Shell.Selected, Message("About Server 1."));

      await fixture.Tick(Snapshots.Server(2));

      Assert.Equal(PageKey.Server(Snapshots.Id(2)), fixture.Shell.Messages.Page);
      Assert.Equal("Server 1 is no longer on this computer.", fixture.Shell.Messages.Current!.Message);
   }



   /// <summary>Nothing is shown for no page or as no message.</summary>
   [Fact]
   public void Messages_RefuseNothing()
   {
      PageMessages messages = new(PageKey.Computer);

      Assert.Equal("page", Assert.Throws<ArgumentNullException>(() => new PageMessages(null!)).ParamName);
      Assert.Throws<ArgumentNullException>(() => messages.Show(null!, Message("Text.")));
      Assert.Throws<ArgumentNullException>(() => messages.Show(PageKey.Computer, null!));
      Assert.Throws<ArgumentNullException>(() => messages.MoveTo(null!));
   }
}
