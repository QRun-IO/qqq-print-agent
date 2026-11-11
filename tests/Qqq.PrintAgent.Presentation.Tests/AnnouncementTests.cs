/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests;

/// <summary>An announcement is a text and one event per thing said: the event comes after the text is whole, once, on the thread that said it.</summary>
public sealed class AnnouncementTests
{
   /// <summary>Nothing has been said at first.</summary>
   [Fact]
   public void New_HasNoText()
   {
      Assert.Equal("", new Announcement().Text);
   }



   /// <summary>The text is in place, and the binding has been told, before the event is raised; the event is raised once, inside the call, on the calling thread.</summary>
   [Fact]
   public void Announce_StoresTheText_ThenRaisesOnce_OnTheCallingThread()
   {
      Announcement announcement = new();
      List<string> seen = [];
      int thread = 0;
      announcement.PropertyChanged += (_, change) => seen.Add("changed " + change.PropertyName + ": " + announcement.Text);
      announcement.Raised += (sender, _) =>
      {
         Assert.Same(announcement, sender);
         thread = Environment.CurrentManagedThreadId;
         seen.Add("raised: " + announcement.Text);
      };

      announcement.Announce("The server passed every check.");

      Assert.Equal(["changed Text: The server passed every check.", "raised: The server passed every check."], seen);
      Assert.Equal(Environment.CurrentManagedThreadId, thread);
      Assert.Equal("The server passed every check.", announcement.Text);
   }



   /// <summary>Each thing said is raised once, with its own text, and the same sentence said again is raised again although the text did not change.</summary>
   [Fact]
   public void Announce_Again_IsRaisedAgain_EvenWithTheSameText()
   {
      Announcement announcement = new();
      List<string> raised = [];
      int changes = 0;
      announcement.PropertyChanged += (_, _) => changes++;
      announcement.Raised += (_, _) => raised.Add(announcement.Text);

      announcement.Announce("First.");
      announcement.Announce("Second.");
      announcement.Announce("Second.");

      Assert.Equal(["First.", "Second.", "Second."], raised);
      Assert.Equal(2, changes);
   }



   /// <summary>Clearing removes the text and says nothing; clearing what is already empty changes nothing.</summary>
   [Fact]
   public void Clear_RemovesTheText_RaisesNothing()
   {
      Announcement announcement = new();
      announcement.Announce("Said.");
      int raised = 0;
      List<string?> changes = [];
      announcement.Raised += (_, _) => raised++;
      announcement.PropertyChanged += (_, change) => changes.Add(change.PropertyName);

      announcement.Clear();
      announcement.Clear();

      Assert.Equal("", announcement.Text);
      Assert.Equal(0, raised);
      Assert.Equal(["Text"], changes);
   }



   /// <summary>An announcement of nothing is refused, and what was said before stays.</summary>
   [Theory]
   [InlineData("")]
   [InlineData("   ")]
   [InlineData("\r\n")]
   public void Announce_NothingToShow_IsRefused(string text)
   {
      Announcement announcement = new();
      announcement.Announce("Said.");
      int raised = 0;
      announcement.Raised += (_, _) => raised++;

      Assert.Equal("text", Assert.Throws<ArgumentException>(() => announcement.Announce(text)).ParamName);
      Assert.Equal("text", Assert.Throws<ArgumentNullException>(() => announcement.Announce(null!)).ParamName);

      Assert.Equal("Said.", announcement.Text);
      Assert.Equal(0, raised);
   }



   /// <summary>Nobody has to listen.</summary>
   [Fact]
   public void Announce_WithNoSubscriber_StoresTheText()
   {
      Announcement announcement = new();

      announcement.Announce("Said.");

      Assert.Equal("Said.", announcement.Text);
   }



   /// <summary>A subscriber that has left is not called.</summary>
   [Fact]
   public void Raised_IsNotSentToASubscriberThatLeft()
   {
      Announcement announcement = new();
      int raised = 0;
      void Count(object? sender, EventArgs e) => raised++;
      announcement.Raised += Count;
      announcement.Announce("One.");
      announcement.Raised -= Count;

      announcement.Announce("Two.");

      Assert.Equal(1, raised);
   }
}
