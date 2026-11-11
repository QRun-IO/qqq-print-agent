/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using CommunityToolkit.Mvvm.ComponentModel;

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// Something that is said to the operator once: the result of a wizard step, the footer's status. It is a text and an
/// event. Whoever has something to say puts the whole sentence together first and then calls <see cref="Announce"/>,
/// which stores the text and raises <see cref="Raised"/> exactly once, after the text is in place, on the thread that
/// called it. A view binds the text and tells a screen reader about it when the event arrives, so a result is read
/// out once and complete, never piece by piece while it is still being put together and never a second time because
/// a property happened to change (ADR-0005, "Accessibility").
/// <para>
/// The same sentence announced again is raised again: an operator who repeats an action and gets the same result has
/// to hear it again. <see cref="Clear"/> removes the text without raising anything, because nothing is being said.
/// It is used from one thread, the one its owner works on; it holds no lock.
/// </para>
/// </summary>
public sealed class Announcement : ObservableObject
{
   /// <summary>Backs <see cref="Text"/>.</summary>
   private string _text = string.Empty;



   /// <summary>Raised once for every <see cref="Announce"/>, after <see cref="Text"/> holds the whole sentence, on the thread that announced it.</summary>
   public event EventHandler? Raised;



   /// <summary>What was announced last, whole; empty when nothing has been announced or it was cleared.</summary>
   public string Text
   {
      get => _text;
      private set => SetProperty(ref _text, value);
   }



   /// <summary>
   /// Says something: stores the text, then raises <see cref="Raised"/> once. The text must be the finished sentence;
   /// one with nothing to show is refused, because an announcement of nothing would be read out as silence.
   /// </summary>
   public void Announce(string text)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(text);
      Text = text;
      Raised?.Invoke(this, EventArgs.Empty);
   }



   /// <summary>Removes the text, for a place that now has nothing to say. Nothing is raised.</summary>
   public void Clear() => Text = string.Empty;
}
