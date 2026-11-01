/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>Cancel a spooler receipt only after verifying its exact QQQ document identity.</summary>
public interface IPrinterJobControl
{
   /// <summary>Cancels one spooler job after proving it is the document QQQ submitted for this job id, so an operator command can never remove someone else's print job that reuses a spooler number.</summary>
   Task CancelAsync(string printerName, string jobId, string spoolerJobId, CancellationToken cancellationToken = default);
}
