/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>One of the wizard's buttons as the view shows it: its label, and whether it can be used now. The view decides neither.</summary>
public sealed record WizardAction(string Label, bool Enabled);
