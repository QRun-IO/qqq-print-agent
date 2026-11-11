/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// Every sentence the shell, the tree, the footer and the tray say to the operator, in one place, so the wording of
/// each can be read and tested together. A sentence says what is known and what to do next, and none says more than
/// the snapshot or the result it is built from. The sentences that 1.x already showed keep their 1.x wording, so an
/// operator who upgrades with one server reads what they read before.
/// </summary>
public static class ShellMessages
{
   /// <summary>What a server is called when the service gave it no name that shows anything.</summary>
   public const string UnnamedServer = "Unnamed server";

   /// <summary>A server that is being removed.</summary>
   public const string BeingRemoved = "Being removed";

   /// <summary>A server whose stored state the service cannot read.</summary>
   public const string StateUnreadable = "Stored data for this server cannot be read · see Connection";

   /// <summary>A server this computer is not paired with.</summary>
   public const string NotPaired = "Not paired";

   /// <summary>A server whose session is not authorized to print, as its node says it.</summary>
   public const string AuthorizationRequired = "Authorization required";

   /// <summary>A server whose administrator holds this computer disconnected. The 1.x sentence.</summary>
   public const string DispatchDisconnected = "Dispatch disconnected by administrator";

   /// <summary>A server whose administrator has paused dispatch. The 1.x sentence.</summary>
   public const string DispatchPaused = "Dispatch paused by administrator";

   /// <summary>A green server. The 1.x sentence.</summary>
   public const string ConnectedAndReady = "Connected and ready";

   /// <summary>An orange server that is connected and has nothing shared, or a shared printer that is not ready. The 1.x sentence.</summary>
   public const string PrintersNeedAttention = "Connected · printers need attention";

   /// <summary>An orange server whose connection is not up.</summary>
   public const string WaitingForConnection = "Waiting for the server connection";

   /// <summary>An orange server that is connected and whose shared printers are ready: the snapshot does not say more.</summary>
   public const string NotReady = "Not ready · see Connection";

   /// <summary>A red server. The 1.x sentence.</summary>
   public const string NeedsAttention = "Disconnected or needs attention";

   /// <summary>A server whose health word this build does not know.</summary>
   public const string HealthNotKnown = "State not known to this version · update the companion";

   /// <summary>A server whose state was read before the service stopped answering.</summary>
   public const string NotKnownNow = "Not known while the local print service is not answering";

   /// <summary>The marker of a server whose development-only certificate exception is switched on.</summary>
   public const string ExceptionMarker = "Certificate exception on";

   /// <summary>The footer and the tray when the service answered, its stored data is ready and the computer has no server.</summary>
   public const string NoServers = "Add a server to start printing";

   /// <summary>The footer before the first read has ended. The 1.x sentence.</summary>
   public const string FooterChecking = "Checking local service…";

   /// <summary>The footer when the service cannot be reached. The 1.x sentence.</summary>
   public const string FooterNotReached = "Local print service unavailable";

   /// <summary>The footer when the service refused to report its state. The 1.x sentence.</summary>
   public const string FooterRefused = "Local service needs attention";

   /// <summary>The footer when an older service answered.</summary>
   public const string FooterOlderService = "Windows service is older than this companion";

   /// <summary>The footer when a newer service answered.</summary>
   public const string FooterNewerService = "Windows service is newer than this companion";

   /// <summary>The footer when the answer could not be read.</summary>
   public const string FooterUnreadable = "Cannot read the local service's answer";

   /// <summary>The footer when the upgrade from 1.x has not finished.</summary>
   public const string FooterUpgradeIncomplete = "Upgrade not finished · not printing";

   /// <summary>The footer when a newer build wrote the stored data.</summary>
   public const string FooterNewerBuild = "Stored data is from a newer build · not printing";

   /// <summary>The footer when the stored data is not ready in a way this build does not know.</summary>
   public const string FooterStoredDataNotReady = "Stored data not ready · not printing";

   /// <summary>What the footer's link says when it opens the Computer page.</summary>
   public const string OpenComputer = "Open Computer";

   /// <summary>The label of the Add server node, and what the footer's link says when it opens the wizard.</summary>
   public const string AddServer = "Add server";

   /// <summary>What a screen reader calls the Add server node while a wizard is open.</summary>
   public const string AddServerInProgress = "Add server, in progress";

   /// <summary>Why a server cannot be reconnected by the operator while its administrator holds it disconnected.</summary>
   public const string ReconnectHeld = "The server's administrator has disconnected this computer. Only they can connect it again.";

   /// <summary>Why a server that is not paired cannot be reconnected.</summary>
   public const string ReconnectNotPaired = "This computer is not paired with this server. Open Connection to pair it.";

   /// <summary>Why a server nobody is signed in to cannot be reconnected.</summary>
   public const string ReconnectNotSignedIn = "Nobody is signed in to this server. Open Connection to sign in.";

   /// <summary>Why there is nothing to sign out of.</summary>
   public const string SignOutNotSignedIn = "Nobody is signed in to this server.";

   /// <summary>Why the only server cannot be moved.</summary>
   public const string MoveOnlyServer = "This is the only server.";

   /// <summary>Why the first server cannot be moved up.</summary>
   public const string MoveAlreadyFirst = "This server is already first.";

   /// <summary>Why the last server cannot be moved down.</summary>
   public const string MoveAlreadyLast = "This server is already last.";

   /// <summary>What is said about a server that was asked for and is not on this computer.</summary>
   public const string ServerGone = "That server is no longer on this computer.";

   /// <summary>What is said about a server that is being removed when something is asked of it.</summary>
   public const string ServerBeingRemoved = "This server is being removed from this computer, so nothing about it can be changed.";



   /// <summary>Why a server cannot be added while the computer has as many as it may hold. A server that is being removed is in the list and counts.</summary>
   public static string ServerLimit { get; } = string.Create(CultureInfo.InvariantCulture,
      $"This computer already has {ManagementProtocol.MaximumServers} servers, the most it can have. A server that is being removed counts until its removal has finished. Remove a server to add another.");



   /// <summary>
   /// A server's name as it is shown. The service holds a name to the name rule where one enters, but what a snapshot
   /// carries is plain text on the wire, so it is given the contract's printable form here, at the rule's own length;
   /// a name that passes the rule is not changed by that. A name with nothing to show becomes
   /// <see cref="UnnamedServer"/>, so a node, a menu line and a message always name something.
   /// </summary>
   public static string Name(string? name)
   {
      string shown = ManagementText.Bounded(name ?? string.Empty, ServerName.MaximumCharacters).Trim();
      return shown.Length == 0 ? UnnamedServer : shown;
   }



   /// <summary>The footer's sentence for one server whose session is not authorized, alone on the computer: the 1.x sentence, naming the server where 1.x named the edition.</summary>
   public static string AuthorizationRequiredFor(string name) => name + " authorization required";



   /// <summary>What a screen reader calls a server's node: its name, its health in words, and the two markers when they apply.</summary>
   public static string ServerNodeName(string name, HealthBadge health, bool exception, bool removing)
      => name + ", " + health.Text + (exception ? ", certificate exception on" : string.Empty) + (removing ? ", being removed" : string.Empty);



   /// <summary>What a screen reader calls one of a server's three pages in the tree.</summary>
   public static string ServerPageNodeName(string page, string name) => page + " for " + name;



   /// <summary>The label of a node of the tree other than a server's own, which is labelled with the server's name. A server's own kind has no label here and is refused.</summary>
   public static string NodeText(PageKind kind) => kind switch
   {
      PageKind.Printers => "Printers",
      PageKind.Jobs => "Jobs",
      PageKind.Connection => "Connection",
      PageKind.Wizard => AddServer,
      PageKind.Computer => "Computer",
      PageKind.Logs => "Logs",
      PageKind.Settings => "Settings",
      _ => throw new ArgumentOutOfRangeException(nameof(kind)),
   };



   /// <summary>The title and the one sentence under it for a page. A server's page names the server.</summary>
   public static PageHeading Heading(PageKey key, string? serverName)
   {
      ArgumentNullException.ThrowIfNull(key);
      string server = serverName ?? UnnamedServer;
      return key.Kind switch
      {
         PageKind.Server => new PageHeading(key, server, "The state of this server and what needs doing."),
         PageKind.Printers => new PageHeading(key, "Printers", $"Choose which printers this computer shares with {server}."),
         PageKind.Jobs => new PageHeading(key, "Jobs", $"Recent jobs this computer received from {server}."),
         PageKind.Connection => new PageHeading(key, "Connection", $"Sign-in, pairing and the connection to {server}."),
         PageKind.Wizard => new PageHeading(key, "Connect to a server", "Check a server, sign in and pair this computer."),
         PageKind.Computer => new PageHeading(key, "Computer", "This computer's details, the Windows service and companion startup."),
         PageKind.Logs => new PageHeading(key, "Logs", "Activity and diagnostics in the AppKit log viewer."),
         _ => new PageHeading(key, "Settings", "Appearance, updates and application information."),
      };
   }



   /// <summary>How many servers are in one state, in words: "1 needs attention", "2 not ready", "3 ready", "1 unknown".</summary>
   public static string Count(HealthLevel level, int count)
   {
      string words = level switch
      {
         HealthLevel.Red => count == 1 ? "needs attention" : "need attention",
         HealthLevel.Orange => "not ready",
         HealthLevel.Green => "ready",
         _ => "unknown",
      };
      return string.Create(CultureInfo.InvariantCulture, $"{count} {words}");
   }



   /// <summary>The footer's sentence for several servers: how many there are and the count in each state, worst first.</summary>
   public static string Servers(int count, IEnumerable<string> counts) => string.Create(CultureInfo.InvariantCulture, $"{count} servers: {string.Join(", ", counts)}");



   /// <summary>What the footer's link says when it opens a server.</summary>
   public static string Open(string name) => "Open " + name;



   /// <summary>One line of the tray menu: a server and its sentence.</summary>
   public static string TrayLine(string name, string sentence) => name + ": " + sentence;



   /// <summary>What the tray tooltip says in place of the servers it has no room for.</summary>
   public static string More(int count) => string.Create(CultureInfo.InvariantCulture, $"+{count} more");



   /// <summary>The message for a command that was refused because another command on the same server, or on the computer, is still running. The explanation is the sentence that command gave.</summary>
   public static FeedbackMessage StillWorking(string holdersSentence) => new("Still working", holdersSentence, FeedbackSeverity.Warning);



   /// <summary>The message for a wizard that was asked for and cannot start now, with the reason.</summary>
   public static FeedbackMessage CannotStart(string reason) => new("Cannot start now", reason, FeedbackSeverity.Warning);



   /// <summary>The message shown when the server whose page was on screen is no longer on this computer and another page took its place.</summary>
   public static FeedbackMessage ServerRemoved(string name) => new("Server removed", name + " is no longer on this computer.", FeedbackSeverity.Informational);
}
