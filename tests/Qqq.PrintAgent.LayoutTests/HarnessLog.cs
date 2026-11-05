/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent;

/// <summary>The harness's per-user log seen through the companion's log interface: debug detail is written as ordinary entries so the real views compile and run against the isolated profile instead of the protected machine log.</summary>
internal sealed class HarnessLog(IActivityLog inner) : IAgentLog
{
   public bool IsDebugEnabled => true;
   public string LogDirectory => inner.LogDirectory;
   public string CurrentLogFilePath => inner.CurrentLogFilePath;
   public void Info(string message) => inner.Info(message);
   public void Warning(string message) => inner.Warning(message);
   public void Error(string message, Exception? exception = null) => inner.Error(message, exception);
   public void Debug(string message) => inner.Info("[DEBUG] " + message);
}
