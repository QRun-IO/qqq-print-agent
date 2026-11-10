/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>One labelled value of a job's summary. A shortened field is limited to three lines with the full text in a tooltip, so a long outcome message cannot push the rest of the inline details out of view.</summary>
public sealed record JobDetailField(string Label, string Value, bool Shortened);
