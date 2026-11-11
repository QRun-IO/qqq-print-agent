/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Shell;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>The way the shell asks for a wizard, as a test sees it: every request is recorded, with the page that was on screen when it was made, and nothing is started.</summary>
internal sealed class RecordingWizard : IWizardLauncher
{
   /// <summary>The shell whose selected page each request is recorded with; a test sets it once the shell exists.</summary>
   internal ShellViewModel? Shell { get; set; }



   /// <summary>Every request so far, oldest first: what was asked for and the page that was on screen at that moment.</summary>
   internal List<string> Requests { get; } = [];



   /// <summary>Records a request for the wizard that adds a server.</summary>
   public void AddServer() => Requests.Add("add on " + Shell?.Selected);



   /// <summary>Records a request for a repair.</summary>
   public void Repair(string serverId) => Requests.Add("repair " + serverId + " on " + Shell?.Selected);



   /// <summary>Records a request for a pairing.</summary>
   public void Pair(string serverId) => Requests.Add("pair " + serverId + " on " + Shell?.Selected);
}
