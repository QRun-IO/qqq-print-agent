/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Presentation.Tests.Fakes;

namespace Qqq.PrintAgent.Presentation.Tests;

/// <summary>Log entries written as a message template and its values: how a template becomes text, and that each level's template method writes at that level for a host that keeps text.</summary>
public sealed class PresentationLogTests
{
   /// <summary>The holes of a template are filled in the order the values are given, whatever the holes are called, and the text around them is kept as written.</summary>
   [Fact]
   public void Template_HolesAreFilledInOrder()
   {
      Assert.Equal("Opened print.example.com after 3 attempts.", PresentationLogTemplate.Render("Opened {Server} after {Attempts} attempts.", ["print.example.com", 3]));
      Assert.Equal("first second", PresentationLogTemplate.Render("{A} {B}", ["first", "second"]));
      Assert.Equal("No holes here.", PresentationLogTemplate.Render("No holes here.", []));
   }



   /// <summary>A value is written the same on every computer, whatever language Windows is set to, and a missing value is written as nothing.</summary>
   [Fact]
   public void Template_ValuesAreWrittenInTheInvariantCulture_AndNullAsNothing()
   {
      CultureInfo before = CultureInfo.CurrentCulture;
      CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
      try
      {
         Assert.Equal("Took 1.5 s on 11/10/2026 10:00:00; reason: .", PresentationLogTemplate.Render("Took {Seconds} s on {When}; reason: {Reason}.", [1.5, new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc), null]));
      }
      finally
      {
         CultureInfo.CurrentCulture = before;
      }
   }



   /// <summary>A hole that was given no value stays as it was written, so a forgotten value shows in the log; a value that has no hole is left out; and a brace inside a value is never taken for a hole.</summary>
   [Fact]
   public void Template_MissingAndSurplusValues_AndBracesInValues()
   {
      Assert.Equal("Opened print.example.com after {Attempts} attempts.", PresentationLogTemplate.Render("Opened {Server} after {Attempts} attempts.", ["print.example.com"]));
      Assert.Equal("Opened print.example.com.", PresentationLogTemplate.Render("Opened {Server}.", ["print.example.com", "surplus"]));
      Assert.Equal("Said {B} and then last.", PresentationLogTemplate.Render("Said {A} and then {B}.", ["{B}", "last"]));
      Assert.Equal("An open brace { is text.", PresentationLogTemplate.Render("An open brace { is text.", ["surplus"]));
   }



   /// <summary>A template and its values are both needed.</summary>
   [Fact]
   public void Template_NeedsATemplateAndValues()
   {
      Assert.Equal("template", Assert.Throws<ArgumentNullException>(() => PresentationLogTemplate.Render(null!, [])).ParamName);
      Assert.Equal("values", Assert.Throws<ArgumentNullException>(() => PresentationLogTemplate.Render("text", null!)).ParamName);
   }



   /// <summary>A host that keeps text and implements only the four text methods gets each templated entry as finished text at the level it was written at.</summary>
   [Fact]
   public void TemplateMethods_WriteTheFinishedTextAtTheirLevel()
   {
      RecordingLog recording = new();
      IPresentationLog log = recording;

      log.Info("Opened {Server}.", "print.example.com");
      log.Warning("Slow answer from {Server} ({Seconds} s).", "print.example.com", 12);
      log.Error("Could not reach {Server}.", "print.example.com");
      log.Debug("Request {Number} to {Server}.", 7, "print.example.com");

      Assert.Equal(["INFO Opened print.example.com.", "WARN Slow answer from print.example.com (12 s).", "ERROR Could not reach print.example.com.", "DEBUG Request 7 to print.example.com."], recording.Entries);
   }
}
