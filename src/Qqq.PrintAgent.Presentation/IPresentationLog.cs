/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// Where presentation logic writes its activity entries. The companion forwards them to the shared activity log with its own
/// prefix; tests record them. Keeping this to four levels means the presentation assembly needs no logging package and no
/// log file, and callers must never pass credentials, enrollment codes or document content to it.
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
}
