/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// Where presentation logic writes its activity entries. The companion forwards them to the shared activity log with its own
/// prefix; tests record them. Keeping this to four levels means the presentation assembly needs no logging package and no
/// log file, and callers must never pass credentials, enrollment codes or document content to it. New code writes an
/// entry as a message template and its values (the second set of methods), never as a string it put together itself;
/// a host that keeps structured entries implements those methods, and one that does not gets the entry as text.
/// </summary>
public interface IPresentationLog
{
   /// <summary>Records one step of normal operation.</summary>
   void Info(string message);



   /// <summary>Records something the operator should notice but that did not stop the work.</summary>
   void Warning(string message);



   /// <summary>Records a failure the operator has to act on.</summary>
   void Error(string message);



   /// <summary>Records request-level detail; the host writes it only while debug logging is switched on.</summary>
   void Debug(string message);



   /// <summary>Records one step of normal operation from a message template and the values of its holes (see <see cref="PresentationLogTemplate"/>).</summary>
   void Info(string template, params object?[] values) => Info(PresentationLogTemplate.Render(template, values));



   /// <summary>Records something the operator should notice, from a message template and the values of its holes.</summary>
   void Warning(string template, params object?[] values) => Warning(PresentationLogTemplate.Render(template, values));



   /// <summary>Records a failure the operator has to act on, from a message template and the values of its holes.</summary>
   void Error(string template, params object?[] values) => Error(PresentationLogTemplate.Render(template, values));



   /// <summary>Records request-level detail from a message template and the values of its holes.</summary>
   void Debug(string template, params object?[] values) => Debug(PresentationLogTemplate.Render(template, values));
}
