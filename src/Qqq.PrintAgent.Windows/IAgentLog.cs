/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;

namespace Qqq.PrintAgent.Windows;

/// <summary>The activity log as the companion uses it: AppKit's levels plus the debug detail that the shared log settings switch on and off.</summary>
public interface IAgentLog : IActivityLog
{
   /// <summary>True while debug logging is enabled in the shared log settings.</summary>
   bool IsDebugEnabled { get; }



   /// <summary>Writes request-level detail only while debug logging is enabled.</summary>
   void Debug(string message);
}
