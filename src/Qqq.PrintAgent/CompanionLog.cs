/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation;

namespace Qqq.PrintAgent;

/// <summary>
/// Forwards what the presentation assembly logs to the shared activity log, marked as coming from the companion so its
/// entries can be told apart from the service's in the one log both write.
/// </summary>
internal sealed class CompanionLog : IPresentationLog
{
   private const string Prefix = "[Companion] ";



   /// <summary>Writes one step of normal operation.</summary>
   public void Info(string message) => App.Log.Info(Prefix + message);



   /// <summary>Writes something the operator should notice.</summary>
   public void Warning(string message) => App.Log.Warning(Prefix + message);



   /// <summary>Writes a failure the operator has to act on.</summary>
   public void Error(string message) => App.Log.Error(Prefix + message);



   /// <summary>Writes request-level detail, which the shared log keeps only while debug logging is on.</summary>
   public void Debug(string message) => App.Log.Debug(Prefix + message);
}
