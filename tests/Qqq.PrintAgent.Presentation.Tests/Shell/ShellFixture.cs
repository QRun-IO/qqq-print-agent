/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Presentation.Shell;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Shell;

/// <summary>
/// A shell over a scripted pipe, for the shell's tests: the real client, the real gate, a wizard launcher that only
/// records, a log that keeps values, and a fixed time format. A test ticks the shell by calling its two reads; no
/// timer and no clock is involved, so no test waits for time to pass.
/// </summary>
internal sealed class ShellFixture : IDisposable
{
   /// <summary>The product's name the tray's tooltip ends with.</summary>
   internal const string Product = "QQQ Print Agent";



   /// <summary>Builds the shell and starts recording what its footer announces.</summary>
   internal ShellFixture()
   {
      Client = new ManagementClient(Pipe.SendAsync, Log);
      Shell = new ShellViewModel(Client, Gate, Wizard, Log, TestData.Time, Product);
      Wizard.Shell = Shell;
      Shell.FooterAnnouncement.Raised += (_, _) => Announced.Add(Shell.FooterAnnouncement.Text + " | footer: " + Shell.Footer.Text);
   }



   /// <summary>The scripted pipe.</summary>
   internal FakePipe Pipe { get; } = new();



   /// <summary>Everything the client and the shell wrote to the log, with the values they handed over.</summary>
   internal TemplateLog Log { get; } = new();



   /// <summary>The one gate.</summary>
   internal OperationGate Gate { get; } = new();



   /// <summary>The requests for a wizard.</summary>
   internal RecordingWizard Wizard { get; } = new();



   /// <summary>The client the shell reads through.</summary>
   internal ManagementClient Client { get; }



   /// <summary>The shell under test.</summary>
   internal ShellViewModel Shell { get; }



   /// <summary>Every footer announcement so far: the text announced and the footer's sentence at the moment it was raised.</summary>
   internal List<string> Announced { get; } = [];



   /// <summary>The identifiers of the tree's nodes, top to bottom, a server's pages after it in brackets when it is expanded.</summary>
   internal string TreeIds => string.Join(' ', Shell.Tree.Nodes.Select(node => node is ServerNode { IsExpanded: true } server
      ? server.AutomationId + "[" + string.Join(',', server.Children.Select(page => page.AutomationId)) + "]"
      : node.AutomationId));



   /// <summary>The names of the servers in the tree, in order.</summary>
   internal string ServerNames => string.Join(',', Shell.Tree.Servers.Select(server => server.Name));



   /// <summary>One tick of the snapshot timer, answered with this snapshot.</summary>
   internal async Task Tick(ServiceSnapshot snapshot)
   {
      Pipe.Answer(Replies.Success(snapshot));
      await Shell.RefreshAsync();
   }



   /// <summary>One tick of the snapshot timer, answered with a snapshot of a ready computer that has these servers.</summary>
   internal Task Tick(params ServerSnapshot[] servers) => Tick(Snapshots.Of(servers));



   /// <summary>One tick of the snapshot timer, answered with whatever the pipe has to say.</summary>
   internal async Task Tick(ManagementReplyParse reply)
   {
      Pipe.Answer(reply);
      await Shell.RefreshAsync();
   }



   /// <summary>One tick of the snapshot timer that fails in the pipe.</summary>
   internal async Task TickFailing(Exception failure)
   {
      Pipe.Fail(failure);
      await Shell.RefreshAsync();
   }



   /// <summary>Disposes the shell.</summary>
   public void Dispose() => Shell.Dispose();
}
