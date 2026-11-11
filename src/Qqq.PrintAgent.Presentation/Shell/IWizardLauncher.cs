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
/// progress it decides what happens to that one. Each call is made on the thread the shell is used on, after the
/// wizard's page has been selected.
/// </summary>
public interface IWizardLauncher
{
   /// <summary>Start the wizard that adds a server, at its first step.</summary>
   void AddServer();



   /// <summary>Start the wizard that signs in again to a server that exists, named by its identifier.</summary>
   void Repair(string serverId);



   /// <summary>Start the wizard that pairs this computer with a server that exists and is signed in, named by its identifier.</summary>
   void Pair(string serverId);
}
