/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

public sealed record PipeRequest(string Operation, string? ServerUrl = null, string? EnrollmentCode = null, string? PrinterId = null, bool? Exposed = null,
   string? JobId = null, string? Format = null, bool ConfirmDuplicateRisk = false, JobQuery? JobQuery = null, LogSettings? LogSettings = null,
   Qqq.PrintAgent.Core.Printing.DeviceProfile? DeviceProfile = null, PrinterProfile? PrinterProfile = null, string? TestProfile = null);



