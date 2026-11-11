/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// The exposure filter's refusal: the job's printer is not shared with QQQ on this computer. Only
/// <see cref="ExposedPrinterBackend"/> can create it, because the constructor is internal, and the filter creates it
/// only before it calls the backend behind it. Whoever catches it therefore knows that nothing was handed to a
/// printing platform and no document bytes were submitted, which is why the worker records such a job as FAILED and
/// not as an uncertain outcome.
/// </summary>
public sealed class PrinterNotExposedException : InvalidOperationException
{
   /// <summary>Creates the refusal. Internal so that no backend outside this assembly can raise it after it has started to print.</summary>
   internal PrinterNotExposedException(string message) : base(message)
   {
   }
}
