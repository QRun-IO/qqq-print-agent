/*
 * Copyright (c) 2026 James Maes
 * SPDX-license-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Cancel a spooler receipt only after verifying its exact QQQ document identity.</summary>
public interface IPrinterJobControl
{
   Task CancelAsync(string printerName, string jobId, string spoolerJobId, CancellationToken cancellationToken = default);
}
