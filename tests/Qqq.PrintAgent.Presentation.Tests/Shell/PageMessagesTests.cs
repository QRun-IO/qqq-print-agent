/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>
/// The message bar by page: a message is shown on the page that caused it and nowhere else; one that has not been on
/// screen is never replaced, so a page's messages wait in order; leaving a page drops only the one that was on
/// screen, and not even that one when it is an error and the shell made the move; and a failure is never dropped
/// without a word, not at the limit of what a page keeps and not when its server goes.
/// </summary>
public sealed class PageMessagesTests
{
   /// <summary>A neutral message for a test.</summary>
   private static FeedbackMessage Message(string text) => new("Title", text, FeedbackSeverity.Informational);



   /// <summary>A failure for a test.</summary>
   private static FeedbackMessage Failure(string text) => new("Failed", text, FeedbackSeverity.Error);



   /// <summary>A failure that is not an error, for a test: a refusal, or something to check.</summary>
   private static FeedbackMessage Warning(string text) => new("Cannot start now", text, FeedbackSeverity.Warning);



   /// <summary>A success for a test.</summary>
   private static FeedbackMessage Success(string text) => new("Done", text, FeedbackSeverity.Success);



   /// <summary>Opens a page, as the operator does, and dismisses its messages one after the other: what the operator reads there, in order. A bar that never comes to the end fails the test here.</summary>
   private static List<string> Read(PageMessages messages, PageKey page)
   {
      List<string> read = [];
      messages.MoveTo(page, byTheOperator: true);
      while(messages.Current is { } current)
      {
         read.Add(current.Message);
         Assert.True(read.Count <= 64, "Dismissing does not come to the end of the messages.");
         messages.Dismiss();
      }
      return read;
   }



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

      messages.MoveTo(PageKey.Logs, byTheOperator: true);
      Assert.Null(messages.Current);

      messages.MoveTo(connection, byTheOperator: true);
      Assert.Equal("Signed out of Server 1.", messages.Current!.Message);

      messages.MoveTo(PageKey.Logs, byTheOperator: true);
      messages.MoveTo(connection, byTheOperator: true);
      Assert.Null(messages.Current);
   }



   /// <summary>
   /// Two results arrive for one page while the operator is elsewhere, a failure and then a success. Neither has
   /// been on screen, so the second does not replace the first: the page shows the failure when it is opened, says
   /// that one more waits, and shows the success when the failure is dismissed.
   /// </summary>
   [Fact]
   public void Message_ThatHasNotBeenOnScreen_IsNeverReplaced_TheyAreShownInOrder()
   {
      var overview = PageKey.Server(Snapshots.Id(2));
      PageMessages messages = new(PageKey.Logs);
      List<string?> raised = [];
      messages.PropertyChanged += (_, change) => raised.Add(change.PropertyName);

      messages.Show(overview, Failure("Two could not be reconnected."));
      messages.Show(overview, Success("Two was renamed."));

      Assert.Null(messages.Current);
      Assert.Equal(0, messages.Waiting);
      Assert.Empty(raised);

      messages.MoveTo(overview, byTheOperator: true);

      Assert.Equal(Failure("Two could not be reconnected."), messages.Current);
      Assert.Equal(1, messages.Waiting);
      Assert.Equal([nameof(PageMessages.Page), nameof(PageMessages.Current), nameof(PageMessages.Waiting)], raised);

      messages.Dismiss();

      Assert.Equal(Success("Two was renamed."), messages.Current);
      Assert.Equal(0, messages.Waiting);

      messages.Dismiss();
      messages.Dismiss();

      Assert.Null(messages.Current);
      Assert.Equal(0, messages.Waiting);
   }



   /// <summary>
   /// What arrives for the page on screen takes the place of the message the bar shows, which the operator has had
   /// in front of them: a result replaces the progress before it, and a failure that was shown the moment it arrived
   /// gives way to the next result. The one exception is a failure that had been waiting: it stays until it is
   /// dismissed, and what arrives meanwhile waits behind it. A failure that waited behind another on the page on
   /// screen has waited too, and stays in the same way when its turn comes.
   /// </summary>
   [Fact]
   public void Message_ForThePageOnScreen_TakesThePlaceOfTheOneShown_UnlessThatIsAFailureThatWaited()
   {
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1));
      PageMessages messages = new(PageKey.Logs);
      messages.Show(jobs, Failure("The retry failed."));
      messages.MoveTo(jobs, byTheOperator: true);

      messages.Show(jobs, Message("Retrying."));
      messages.Show(jobs, Success("Retried."));

      Assert.Equal("The retry failed.", messages.Current!.Message);
      Assert.Equal(2, messages.Waiting);

      messages.Dismiss();
      Assert.Equal("Retrying.", messages.Current!.Message);
      messages.Show(jobs, Message("Another."));

      Assert.Equal("Retried.", messages.Current!.Message);
      Assert.Equal(1, messages.Waiting);

      messages.Dismiss();
      Assert.Equal("Another.", messages.Current!.Message);
      messages.Show(jobs, Failure("Shown the moment it arrived."));
      Assert.Equal("Shown the moment it arrived.", messages.Current!.Message);
      messages.Show(jobs, Success("Done."));

      Assert.Equal("Done.", messages.Current!.Message);
      Assert.Equal(0, messages.Waiting);

      messages.MoveTo(PageKey.Logs, byTheOperator: true);
      messages.Show(jobs, Failure("Waited while the page was not on screen."));
      messages.MoveTo(jobs, byTheOperator: true);
      messages.Show(jobs, Failure("Waited behind it on the page on screen."));
      messages.Dismiss();
      messages.Show(jobs, Success("After both."));

      Assert.Equal("Waited behind it on the page on screen.", messages.Current!.Message);
      Assert.Equal(1, messages.Waiting);
   }



   /// <summary>When the operator leaves a page, the message that was on screen is dropped, an error too, and the ones that were not are kept: they are shown when the page is opened again.</summary>
   [Fact]
   public void Message_OnLeaving_OnlyTheOneThatWasOnScreenIsDropped()
   {
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1));
      PageMessages messages = new(PageKey.Logs);
      messages.Show(jobs, Failure("First."));
      messages.Show(jobs, Failure("Second."));
      messages.MoveTo(jobs, byTheOperator: true);
      Assert.Equal("First.", messages.Current!.Message);

      messages.MoveTo(PageKey.Settings, byTheOperator: true);

      Assert.Null(messages.Current);
      Assert.Equal(0, messages.Waiting);

      messages.MoveTo(jobs, byTheOperator: true);

      Assert.Equal("Second.", messages.Current!.Message);
      Assert.Equal(0, messages.Waiting);
      Assert.Empty(Read(messages, PageKey.Settings));
      Assert.Empty(Read(messages, jobs));
   }



   /// <summary>A message shown the moment it arrived is replaced by the next. Closing the bar removes it. Going to the page that is already on screen keeps it. The bar tells its bindings what changed.</summary>
   [Fact]
   public void Message_IsReplacedDismissedAndKept()
   {
      PageMessages messages = new(PageKey.Computer);
      List<string?> raised = [];
      messages.PropertyChanged += (_, change) => raised.Add(change.PropertyName);

      messages.Show(PageKey.Computer, Message("First."));
      messages.Show(PageKey.Computer, Message("Second."));
      messages.MoveTo(PageKey.Computer, byTheOperator: true);

      Assert.Equal("Second.", messages.Current!.Message);
      Assert.Equal([nameof(PageMessages.Current), nameof(PageMessages.Current)], raised);

      messages.Dismiss();
      Assert.Null(messages.Current);
      messages.MoveTo(PageKey.Logs, byTheOperator: true);
      messages.MoveTo(PageKey.Computer, byTheOperator: true);
      Assert.Null(messages.Current);
      Assert.Equal([nameof(PageMessages.Current), nameof(PageMessages.Current), nameof(PageMessages.Current), nameof(PageMessages.Page), nameof(PageMessages.Page)], raised);
   }



   /// <summary>
   /// A page keeps eight messages. A ninth that is not a failure is not kept. A failure that arrives then takes the
   /// place of the oldest waiting message that is not a failure. Each time a result is not kept, the page's own
   /// note counts it, and stands where the first one was lost. The note is not one of the eight: when a message has
   /// been read and seven are left beside the note, the next one is kept.
   /// </summary>
   [Fact]
   public void Messages_APageKeepsEight_WhatIsNotAFailureGivesWay_AndThePageSaysHowMany()
   {
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1));
      PageMessages messages = new(PageKey.Logs);
      for(int number = 1; number <= PageMessages.MaximumPerPage; number++)
      {
         messages.Show(jobs, Success(string.Create(CultureInfo.InvariantCulture, $"Success {number}.")));
      }

      messages.Show(jobs, Success("Success 9."));
      messages.Show(jobs, Failure("Failure 1."));
      messages.Show(jobs, Message("Neutral."));

      Assert.Equal(8, PageMessages.MaximumPerPage);
      messages.MoveTo(jobs, byTheOperator: true);
      Assert.Equal(8, messages.Waiting);
      Assert.Equal(
      [
         "Success 2.", "Success 3.", "Success 4.", "Success 5.", "Success 6.", "Success 7.", "Success 8.",
         "This page already held as many results as it keeps. Not kept: 3. Failures among them: 0.", "Failure 1.",
      ], Read(messages, jobs));
      Assert.Equal(new FeedbackMessage("Results not kept", "This page already held as many results as it keeps. Not kept: 1. Failures among them: 0.", FeedbackSeverity.Informational), ShellMessages.ResultsNotKept(1, 0));

      messages.MoveTo(PageKey.Logs, byTheOperator: true);
      for(int number = 1; number <= PageMessages.MaximumPerPage + 1; number++)
      {
         messages.Show(jobs, Success(string.Create(CultureInfo.InvariantCulture, $"Later {number}.")));
      }
      messages.MoveTo(jobs, byTheOperator: true);
      messages.MoveTo(PageKey.Logs, byTheOperator: true);
      messages.Show(jobs, Success("Kept, because seven were left beside the note."));

      Assert.Equal(
      [
         "Later 2.", "Later 3.", "Later 4.", "Later 5.", "Later 6.", "Later 7.", "Later 8.",
         "This page already held as many results as it keeps. Not kept: 1. Failures among them: 0.", "Kept, because seven were left beside the note.",
      ], Read(messages, jobs));
   }



   /// <summary>
   /// When every message a page keeps is a failure, one more failure is not kept either, but not without a word: the
   /// page's note says that a failure was among the results it did not keep, as a warning, and like any failure that
   /// waited it stays on screen until it is dismissed. Nothing that is on screen gives way to make room.
   /// </summary>
   [Fact]
   public void Messages_AFailureThatIsNotKept_IsCounted_AsAFailure()
   {
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1));
      PageMessages messages = new(PageKey.Logs);
      messages.Show(jobs, Failure("Failure 1."));
      messages.MoveTo(jobs, byTheOperator: true);
      for(int number = 2; number <= PageMessages.MaximumPerPage; number++)
      {
         messages.Show(jobs, Failure(string.Create(CultureInfo.InvariantCulture, $"Failure {number}.")));
      }

      messages.Show(jobs, Success("Success."));
      messages.Show(jobs, Failure("Failure 9."));
      messages.Show(jobs, Failure("Failure 10."));

      Assert.Equal("Failure 1.", messages.Current!.Message);
      Assert.Equal(8, messages.Waiting);
      for(int number = 1; number <= PageMessages.MaximumPerPage; number++)
      {
         Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"Failure {number}."), messages.Current!.Message);
         messages.Dismiss();
      }
      Assert.Equal(new FeedbackMessage("Results not kept", "This page already held as many results as it keeps. Not kept: 3. Failures among them: 2.", FeedbackSeverity.Warning), messages.Current);

      messages.Show(jobs, Success("After the note."));

      Assert.Equal("Results not kept", messages.Current!.Title);
      Assert.Equal(1, messages.Waiting);
      messages.Dismiss();
      Assert.Equal("After the note.", messages.Current!.Message);
   }



   /// <summary>
   /// When a server has gone its pages cannot be opened again. The failures that were waiting under them are shown
   /// on the page the shell shows instead, each saying which server it was about; a message on screen that was not
   /// an error and what was not a failure are dropped. A failure reported for that server later is shown on the page on
   /// screen in the same way, and anything else reported for it is dropped. When the server is listed again its
   /// pages keep their own messages again.
   /// </summary>
   [Fact]
   public void Message_OfAServerThatWent_AFailureIsShownOnThePageShownInstead_TheRestIsDropped()
   {
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1));
      var connection = PageKey.ForServer(PageKind.Connection, Snapshots.Id(1));
      var other = PageKey.Server(Snapshots.Id(2));
      PageMessages messages = new(jobs);
      messages.Show(jobs, Warning("On screen."));
      messages.Show(connection, Success("Signed out."));
      messages.Show(connection, Failure("The sign-out failed."));
      messages.Show(other, Message("Another server's."));

      messages.ServersWent(serverId => string.Equals(serverId, Snapshots.Id(2), StringComparison.Ordinal), [KeyValuePair.Create(Snapshots.Id(1), "Warehouse")], other);

      Assert.Equal(other, messages.Page);
      Assert.Equal("Another server's.", messages.Current!.Message);
      Assert.Equal(1, messages.Waiting);
      messages.Dismiss();
      Assert.Equal(new FeedbackMessage("Failed", "About Warehouse, which is no longer on this computer: The sign-out failed.", FeedbackSeverity.Error), messages.Current);
      messages.Dismiss();
      Assert.Null(messages.Current);

      messages.Show(connection, Success("Renamed."));
      Assert.Null(messages.Current);
      messages.Show(jobs, Failure("The retry failed."));
      messages.Show(other, Message("Its own."));

      Assert.Equal("About Warehouse, which is no longer on this computer: The retry failed.", messages.Current!.Message);
      Assert.Equal(1, messages.Waiting);
      Assert.Empty(Read(messages, connection));
      Assert.Empty(Read(messages, jobs));

      messages.ServersWent(_ => true, [], PageKey.Logs);
      messages.Show(connection, Failure("Under its own page again."));

      Assert.Null(messages.Current);
      Assert.Equal(["Under its own page again."], Read(messages, connection));
      Assert.Throws<ArgumentNullException>(() => ShellMessages.AboutAServerThatWent(null!, "Warehouse"));
   }



   /// <summary>
   /// The server whose page is on screen goes, which is a move the shell makes. An error that was on screen there
   /// is not lost, and its page cannot be opened again: it is shown on the page the shell shows instead, before the
   /// failures that waited, saying which server it was about. A warning that was on screen is dropped.
   /// </summary>
   [Theory]
   [InlineData(FeedbackSeverity.Error, true)]
   [InlineData(FeedbackSeverity.Warning, false)]
   public void Message_OnScreen_OfAServerThatWent_IsShownOnThePageShownInstead_OnlyWhenItIsAnError(FeedbackSeverity severity, bool carried)
   {
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1));
      var connection = PageKey.ForServer(PageKind.Connection, Snapshots.Id(1));
      var other = PageKey.Server(Snapshots.Id(2));
      PageMessages messages = new(jobs);
      messages.Show(jobs, new FeedbackMessage("Title", "On screen.", severity));
      messages.Show(connection, Failure("The sign-out failed."));

      messages.ServersWent(serverId => string.Equals(serverId, Snapshots.Id(2), StringComparison.Ordinal), [KeyValuePair.Create(Snapshots.Id(1), "Warehouse")], other);

      string[] waited = ["About Warehouse, which is no longer on this computer: The sign-out failed."];
      Assert.Equal(carried ? ["About Warehouse, which is no longer on this computer: On screen.", .. waited] : waited, Read(messages, other), StringComparer.Ordinal);
   }



   /// <summary>
   /// When the shell itself takes the operator to another page, the operator did not choose to leave and may not
   /// have read the bar. An error that was on screen stays under its page and is shown when the operator next opens
   /// it. It has waited then, so what arrives does not cover it, and it goes when the operator leaves. A warning, a
   /// notice and a success that were on screen are dropped by the same move: a refusal such as "Cannot start now"
   /// would otherwise be shown again when it is no longer true.
   /// </summary>
   [Theory]
   [InlineData(FeedbackSeverity.Error, true)]
   [InlineData(FeedbackSeverity.Warning, false)]
   [InlineData(FeedbackSeverity.Informational, false)]
   [InlineData(FeedbackSeverity.Success, false)]
   public void Message_OnScreen_WhenTheShellMovesTheOperatorAway_IsKeptOnlyWhenItIsAnError(FeedbackSeverity severity, bool kept)
   {
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1));
      FeedbackMessage onScreen = new("Title", "On screen when the shell moved.", severity);
      PageMessages messages = new(jobs);
      messages.Show(jobs, onScreen);

      messages.MoveTo(PageKey.Computer, byTheOperator: false);

      Assert.Null(messages.Current);
      messages.MoveTo(PageKey.Logs, byTheOperator: false);
      messages.MoveTo(jobs, byTheOperator: true);
      Assert.Equal(kept ? onScreen : null, messages.Current);

      messages.Show(jobs, Success("Arrived after the operator came back."));

      Assert.Equal(kept ? onScreen.Message : "Arrived after the operator came back.", messages.Current!.Message);
      Assert.Equal(kept ? 1 : 0, messages.Waiting);

      messages.MoveTo(PageKey.Computer, byTheOperator: true);
      messages.MoveTo(jobs, byTheOperator: true);

      Assert.Equal(kept ? "Arrived after the operator came back." : null, messages.Current?.Message);
      Assert.Equal(0, messages.Waiting);
   }



   /// <summary>
   /// The servers that went are remembered so that a failure reported for one of them later can say which server it
   /// was about. A server that goes a second time under a new name is known by that name. The last thirty-two are
   /// remembered: a failure reported for a server that went before those waits under its own page.
   /// </summary>
   [Fact]
   public void Messages_RememberTheLastServersThatWent()
   {
      var first = PageKey.Server(Snapshots.Id(40));
      PageMessages messages = new(PageKey.Logs);
      messages.ServersWent(_ => false, [KeyValuePair.Create(Snapshots.Id(40), "An earlier name")], PageKey.Logs);
      messages.ServersWent(_ => false, [KeyValuePair.Create(Snapshots.Id(40), "A later name")], PageKey.Logs);

      messages.Show(first, Failure("Late."));

      Assert.Equal(["About A later name, which is no longer on this computer: Late."], Read(messages, PageKey.Logs));

      messages.ServersWent(_ => false, [.. Enumerable.Range(1, 32).Select(number => KeyValuePair.Create(Snapshots.Id(number), string.Create(CultureInfo.InvariantCulture, $"Server {number}")))], PageKey.Logs);
      messages.Show(first, Failure("For the one that is no longer remembered."));
      messages.Show(PageKey.Server(Snapshots.Id(1)), Failure("For one that still is."));

      Assert.Equal(["About Server 1, which is no longer on this computer: For one that still is."], Read(messages, PageKey.Logs));
      Assert.Equal(["For the one that is no longer remembered."], Read(messages, first));
   }



   /// <summary>
   /// A failure waits under server Two's overview while server One's page is on screen, and One is removed. The
   /// shell shows Two's overview, which brings the failure on screen, and says in the same pass that One went. The
   /// second does not cover the first: the failure is what the bar shows, and the notice waits behind it.
   /// </summary>
   [Fact]
   public async Task Message_ThatAServerWent_WaitsBehindAFailureOfThePageShownInstead()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "One"), Snapshots.Server(2, "Two"));
      var overviewOfTwo = PageKey.Server(Snapshots.Id(2));
      fixture.Shell.Messages.Show(overviewOfTwo, Failure("Two could not be reconnected."));
      List<string?> shown = [];
      fixture.Shell.Messages.PropertyChanged += (_, change) =>
      {
         if(string.Equals(change.PropertyName, nameof(PageMessages.Current), StringComparison.Ordinal))
         {
            shown.Add(fixture.Shell.Messages.Current?.Message);
         }
      };

      await fixture.Tick(Snapshots.Server(2, "Two"));

      Assert.Equal(overviewOfTwo, fixture.Shell.Selected);
      Assert.Equal(["Two could not be reconnected."], shown);
      Assert.Equal(1, fixture.Shell.Messages.Waiting);

      fixture.Shell.Messages.Dismiss();

      Assert.Equal(new FeedbackMessage("Server removed", "One is no longer on this computer.", FeedbackSeverity.Informational), fixture.Shell.Messages.Current);
      fixture.Shell.Select(PageKey.Logs);
      Assert.True(fixture.Shell.Select(overviewOfTwo));
      Assert.Null(fixture.Shell.Messages.Current);
   }



   /// <summary>
   /// The server whose page is on screen goes while results still wait under its other pages. The message that was
   /// on screen is dropped, and so is a success that waited. The failure that waited is not lost: it is shown on the
   /// neighbour's overview, after the notice that the server went, and says which server it was about.
   /// </summary>
   [Fact]
   public async Task Message_OfTheSelectedServerThatWent_AFailureFollowsToItsNeighbour_TheRestDoesNot()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "One"), Snapshots.Server(2, "Two"));
      fixture.Shell.Messages.Show(fixture.Shell.Selected, Message("About One, on screen."));
      fixture.Shell.Messages.Show(PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1)), Success("The job was retried."));
      fixture.Shell.Messages.Show(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1)), Failure("The sign-out failed."));

      await fixture.Tick(Snapshots.Server(2, "Two"));

      Assert.Equal(PageKey.Server(Snapshots.Id(2)), fixture.Shell.Messages.Page);
      Assert.Equal("One is no longer on this computer.", fixture.Shell.Messages.Current!.Message);
      Assert.Equal(1, fixture.Shell.Messages.Waiting);
      fixture.Shell.Messages.Dismiss();
      Assert.Equal(new FeedbackMessage("Failed", "About One, which is no longer on this computer: The sign-out failed.", FeedbackSeverity.Error), fixture.Shell.Messages.Current);
      fixture.Shell.Messages.Dismiss();
      Assert.Null(fixture.Shell.Messages.Current);
      Assert.DoesNotContain(fixture.Log.Entries, entry => entry.Contains("One", StringComparison.Ordinal) || entry.Contains("sign-out", StringComparison.Ordinal));
   }



   /// <summary>
   /// A server goes while another page is on screen. A failure that waited under one of its pages is shown on the
   /// page on screen, saying which server it was about, and a success that waited is dropped. It is not what the
   /// operator was doing on that page, so the next message of that page does not cover it: it stays until it is
   /// dismissed. A failure reported for that server afterwards is shown the same way: it is never kept where no
   /// page can show it.
   /// </summary>
   [Fact]
   public async Task Message_OfAServerThatWent_WhileAnotherPageIsOnScreen_AFailureIsShownThere()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "One"), Snapshots.Server(2, "Two"));
      fixture.Shell.Select(PageKey.Logs);
      fixture.Shell.Messages.Show(PageKey.ForServer(PageKind.Jobs, Snapshots.Id(1)), Success("The job was retried."));
      fixture.Shell.Messages.Show(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1)), Failure("The sign-out failed."));

      await fixture.Tick(Snapshots.Server(2, "Two"));

      Assert.Equal(PageKey.Logs, fixture.Shell.Selected);
      Assert.Equal("About One, which is no longer on this computer: The sign-out failed.", fixture.Shell.Messages.Current!.Message);
      Assert.Equal(0, fixture.Shell.Messages.Waiting);

      fixture.Shell.Messages.Show(PageKey.Logs, Message("About the logs."));

      Assert.Equal("About One, which is no longer on this computer: The sign-out failed.", fixture.Shell.Messages.Current!.Message);
      Assert.Equal(1, fixture.Shell.Messages.Waiting);

      fixture.Shell.Messages.Dismiss();
      fixture.Shell.Messages.Dismiss();
      await fixture.Tick(Snapshots.Server(2, "Two"));
      fixture.Shell.Messages.Show(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1)), Failure("The rename failed."));

      Assert.Equal("About One, which is no longer on this computer: The rename failed.", fixture.Shell.Messages.Current!.Message);
      Assert.False(fixture.Shell.Select(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1))));
   }



   /// <summary>When the only server goes, the wizard's page is shown. It shows no notice that the server went, but a failure that waited under the server's pages is not lost: it is shown there, saying which server it was about.</summary>
   [Fact]
   public async Task Message_OfTheLastServerThatWent_AFailureIsShownOnTheWizardsPage()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1, "One"));
      fixture.Shell.Messages.Show(PageKey.ForServer(PageKind.Connection, Snapshots.Id(1)), Failure("The sign-out failed."));

      await fixture.Tick();

      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      Assert.Equal(["add on Wizard"], fixture.Wizard.Requests);
      Assert.Equal("About One, which is no longer on this computer: The sign-out failed.", fixture.Shell.Messages.Current!.Message);
      Assert.Equal(0, fixture.Shell.Messages.Waiting);
   }



   /// <summary>
   /// A move the shell makes itself drops no message that has not been on screen, and no error that was. The
   /// operator has a page open under which two failures waited, so the first is on screen and the second is not,
   /// when the wizard ends and the shell shows the Computer page; and again when the shell leaves the Computer page
   /// for the server the wizard added. Each time, when the operator opens the page again, the error that was on
   /// screen is shown first, because the operator may not have read it, and the one that was not on screen after
   /// it. A move the operator makes in between drops the error that was on screen then.
   /// </summary>
   [Fact]
   public async Task Messages_SurviveAMoveTheShellMakes_TheErrorThatWasOnScreen_AndTheOnesThatWereNot()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2));
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(2));
      fixture.Shell.StartRepair(Snapshots.Id(1));
      fixture.Shell.Messages.Show(jobs, Failure("The first retry failed."));
      fixture.Shell.Messages.Show(jobs, Failure("The second retry failed."));
      fixture.Shell.Messages.Show(PageKey.Computer, Failure("The first save failed."));
      fixture.Shell.Messages.Show(PageKey.Computer, Failure("The second save failed."));
      fixture.Shell.Select(jobs);
      Assert.Equal("The first retry failed.", fixture.Shell.Messages.Current!.Message);

      fixture.Shell.WizardEnded(PageKey.Computer);

      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Equal("The first save failed.", fixture.Shell.Messages.Current!.Message);

      fixture.Shell.StartAddServer();
      fixture.Shell.WizardEnded(PageKey.Server(Snapshots.Id(3)));
      Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
      Assert.Equal("The second save failed.", fixture.Shell.Messages.Current!.Message);
      fixture.Shell.Messages.Show(PageKey.Computer, Failure("The third save failed."));
      await fixture.Tick(Snapshots.Server(1), Snapshots.Server(2), Snapshots.Server(3));

      Assert.Equal(PageKey.Server(Snapshots.Id(3)), fixture.Shell.Selected);
      Assert.Null(fixture.Shell.Messages.Current);
      fixture.Shell.Select(PageKey.Computer);
      Assert.Equal(["The second save failed.", "The third save failed."], Read(fixture.Shell.Messages, PageKey.Computer));
      fixture.Shell.Select(jobs);
      Assert.Equal(["The first retry failed.", "The second retry failed."], Read(fixture.Shell.Messages, jobs));
   }



   /// <summary>
   /// Every other move the shell makes keeps an error that was on screen under the page it was on, and the operator
   /// finds it there: when the window lands, when a wizard ends with nothing to show and the window goes where it
   /// would land, and when a wizard ends naming a server no snapshot lists yet and the Computer page stands in. A
   /// refusal that was on screen when the window landed is not shown again: it is no longer true.
   /// </summary>
   [Fact]
   public async Task Error_OnScreen_IsKeptUnderItsPage_WhenTheWindowLands_AndWhenAWizardEnds()
   {
      using(ShellFixture fixture = new())
      {
         fixture.Shell.Messages.Show(PageKey.Computer, Failure("The service could not be started."));
         await fixture.Tick(Snapshots.Server(1));

         Assert.Equal(PageKey.ForServer(PageKind.Printers, Snapshots.Id(1)), fixture.Shell.Selected);
         Assert.Null(fixture.Shell.Messages.Current);
         fixture.Shell.Select(PageKey.Computer);
         Assert.Equal("The service could not be started.", fixture.Shell.Messages.Current!.Message);
      }
      using(ShellFixture fixture = new())
      {
         Assert.False(fixture.Shell.StartAddServer());
         Assert.Equal(("Cannot start now", FeedbackSeverity.Warning), (fixture.Shell.Messages.Current!.Title, fixture.Shell.Messages.Current.Severity));
         await fixture.Tick(Snapshots.Server(1));

         fixture.Shell.Select(PageKey.Computer);
         Assert.Null(fixture.Shell.Messages.Current);
      }
      using(ShellFixture fixture = new())
      {
         await fixture.Tick(Snapshots.Server(1));
         fixture.Shell.StartAddServer();
         fixture.Shell.Messages.Show(PageKey.Wizard, Failure("Shown on the wizard's page."));

         fixture.Shell.WizardEnded();

         Assert.Equal(PageKey.ForServer(PageKind.Printers, Snapshots.Id(1)), fixture.Shell.Selected);
         Assert.Null(fixture.Shell.Messages.Current);
         fixture.Shell.Select(PageKey.Wizard);
         Assert.Equal("Shown on the wizard's page.", fixture.Shell.Messages.Current!.Message);

         fixture.Shell.WizardEnded(PageKey.Server(Snapshots.Id(2)));

         Assert.Equal(PageKey.Computer, fixture.Shell.Selected);
         Assert.Null(fixture.Shell.Messages.Current);
         fixture.Shell.Select(PageKey.Wizard);
         Assert.Equal("Shown on the wizard's page.", fixture.Shell.Messages.Current!.Message);
      }
   }



   /// <summary>
   /// The server whose page is on screen goes while an error is on screen there. The shell takes the operator to
   /// the neighbour's overview, and the error goes with them, because its own page cannot be opened again: it is
   /// shown after the notice that the server went and says which server it was about. When the server was the
   /// last one, it is shown on the wizard's page.
   /// </summary>
   [Fact]
   public async Task Error_OnScreen_OfTheSelectedServerThatWent_IsShownOnThePageShownInstead()
   {
      using(ShellFixture fixture = new())
      {
         await fixture.Tick(Snapshots.Server(1, "One"), Snapshots.Server(2, "Two"));
         fixture.Shell.Messages.Show(fixture.Shell.Selected, Failure("The printer could not be shared."));

         await fixture.Tick(Snapshots.Server(2, "Two"));

         Assert.Equal(PageKey.Server(Snapshots.Id(2)), fixture.Shell.Selected);
         Assert.Equal(["One is no longer on this computer.", "About One, which is no longer on this computer: The printer could not be shared."],
            Read(fixture.Shell.Messages, PageKey.Server(Snapshots.Id(2))));
      }
      using(ShellFixture fixture = new())
      {
         await fixture.Tick(Snapshots.Server(1, "One"));
         fixture.Shell.Messages.Show(fixture.Shell.Selected, Failure("The printer could not be shared."));

         await fixture.Tick();

         Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
         Assert.Equal(["About One, which is no longer on this computer: The printer could not be shared."], Read(fixture.Shell.Messages, PageKey.Wizard));
      }
   }



   /// <summary>
   /// A move the operator makes drops the message that was on screen, an error too: choosing another page, asking
   /// for a wizard, and going back to the wizard that is open when no server can be added.
   /// </summary>
   [Fact]
   public async Task Error_OnScreen_IsDropped_ByEveryMoveTheOperatorMakes()
   {
      using ShellFixture fixture = new();
      await fixture.Tick(Snapshots.WithServers(8));
      var jobs = PageKey.ForServer(PageKind.Jobs, Snapshots.Id(2));
      fixture.Shell.Select(jobs);

      fixture.Shell.Messages.Show(jobs, Failure("Left by choosing a page."));
      fixture.Shell.Select(PageKey.Logs);
      fixture.Shell.Select(jobs);
      Assert.Null(fixture.Shell.Messages.Current);

      fixture.Shell.Messages.Show(jobs, Failure("Left by asking for a wizard."));
      Assert.True(fixture.Shell.StartRepair(Snapshots.Id(1)));
      fixture.Shell.Select(jobs);
      Assert.Null(fixture.Shell.Messages.Current);

      fixture.Shell.Messages.Show(jobs, Failure("Left by going back to the open wizard."));
      Assert.True(fixture.Shell.StartAddServer());
      Assert.Equal(PageKey.Wizard, fixture.Shell.Selected);
      fixture.Shell.Select(jobs);
      Assert.Null(fixture.Shell.Messages.Current);
   }



   /// <summary>Nothing is shown for no page or as no message.</summary>
   [Fact]
   public void Messages_RefuseNothing()
   {
      PageMessages messages = new(PageKey.Computer);

      Assert.Equal("page", Assert.Throws<ArgumentNullException>(() => new PageMessages(null!)).ParamName);
      Assert.Throws<ArgumentNullException>(() => messages.Show(null!, Message("Text.")));
      Assert.Throws<ArgumentNullException>(() => messages.Show(PageKey.Computer, null!));
      Assert.Throws<ArgumentNullException>(() => messages.MoveTo(null!, byTheOperator: true));
   }
}
