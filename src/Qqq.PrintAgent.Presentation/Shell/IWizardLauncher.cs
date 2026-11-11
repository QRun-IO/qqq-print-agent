/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// How the shell asks for the connection wizard. The shell decides that the wizard's page is what the window shows
/// and that a wizard may start now (the service answered, its stored data is ready, the computer has room for another
/// server, the server named exists and is not being removed); it then shows the wizard's page and calls one of these.
/// Whoever implements this owns the one wizard: it starts it in the mode asked for, and when a wizard is already in
/// progress it decides what happens to that one.
/// <para>
/// Every call is a request for a wizard to start. None is made to show a wizard that is open: when the operator
/// goes back to the wizard's page through the Add server node (<see cref="ShellViewModel.Select"/>) while one is
/// open, the shell shows the page and calls nothing. Each call is made on the thread the shell is used on, after
/// the wizard's page has been selected and <see cref="ShellViewModel.WizardOpen"/> is true, and never from inside
/// <see cref="ShellViewModel.WizardEnded"/>, so a wizard that tells the shell it has ended is not asked for again
/// while it is still ending.
/// </para>
/// <para>
/// When a wizard has ended, its owner calls <see cref="ShellViewModel.WizardEnded"/> once. A wizard that ended with
/// a server on this computer, one it added or one it left in place when the operator chose to keep it, passes that
/// server's page, also when no snapshot lists the server yet: the shell then shows the server as soon as one does.
/// A wizard that ended with nothing to show passes no page.
/// </para>
/// </summary>
public interface IWizardLauncher
{
   /// <summary>
   /// Start the wizard that adds a server, at its first step. It is called when a server can be added and either
   /// the operator asked for it (<see cref="ShellViewModel.StartAddServer"/>, or the Add server node while no
   /// wizard is open), which can be while another wizard is open, or the computer has no server and no wizard is
   /// open: the first time the service answers with its stored data ready, when the last server goes, and on the
   /// first snapshot after a wizard ended with nothing to show.
   /// </summary>
   void AddServer();



   /// <summary>Start the wizard that signs in again to a server that exists, named by its identifier. It is called only when the operator asked for it (<see cref="ShellViewModel.StartRepair"/>) and the server is listed and is not being removed; how many servers the computer has does not matter.</summary>
   void Repair(string serverId);



   /// <summary>Start the wizard that pairs this computer with a server that exists and is signed in, named by its identifier. It is called only when the operator asked for it (<see cref="ShellViewModel.StartPairing"/>), under the same conditions as <see cref="Repair"/>.</summary>
   void Pair(string serverId);
}
