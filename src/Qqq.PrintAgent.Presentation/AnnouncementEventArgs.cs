/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// What <see cref="Announcement.Raised"/> says: the sentence that was announced, whole. A listener reads the sentence
/// from here and not from <see cref="Announcement.Text"/>, which holds what was announced last: when a listener
/// announces something more from inside the event, the listeners after it are still told this sentence, and are
/// told the other one by the event raised for it.
/// </summary>
public sealed class AnnouncementEventArgs : EventArgs
{
   /// <summary>Records the sentence.</summary>
   public AnnouncementEventArgs(string text)
   {
      Text = text;
   }



   /// <summary>The sentence this event announces, whole.</summary>
   public string Text { get; }
}
