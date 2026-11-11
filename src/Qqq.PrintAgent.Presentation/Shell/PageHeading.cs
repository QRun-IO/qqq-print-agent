/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>What stands at the top of the page on screen: which page it is, its title and the one sentence under the title that says what the page is for.</summary>
public sealed record PageHeading(PageKey Key, string Title, string Description);
