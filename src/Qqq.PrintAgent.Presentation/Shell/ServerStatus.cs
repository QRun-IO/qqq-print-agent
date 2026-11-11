/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// One server's state in words, worked out once from its snapshot and used by its node, the footer and the tray: its
/// name as it is shown, its health as a badge, the sentence beside its name, and the sentence that stands for it when
/// it is the only server (<see cref="Alone"/>, which for a paired server in the states 1.x had is what the footer of
/// 1.x said).
/// <para>
/// The sentence follows the footer rule of 1.x for a server that is paired: the health's own sentence, replaced by
/// the more exact reason when the administrator has disconnected or paused dispatch, and by the missing
/// authorization before either. Beside the name the missing authorization is said as what the snapshot shows to be
/// missing: "Sign in required" when nobody is signed in, "Authorization required" when somebody is and the session
/// is not authorized; alone in the footer both keep the words of 1.x. Three states 1.x did not have come first: a
/// server that is being removed, one whose stored state cannot be read, and one this computer is not paired with.
/// No sentence says more than the snapshot:
/// an orange server is said to be connected with printers needing attention, as in 1.x, only when its transport is
/// connected and it has nothing shared or a shared printer that is not ready; and a health word this build does
/// not know is said to be unknown.
/// </para>
/// </summary>
public sealed record ServerStatus(string ServerId, string Name, HealthBadge Health, string Sentence, string Alone)
{
   /// <summary>The transport state that means the connection is up.</summary>
   private const string Connected = "CONNECTED";

   /// <summary>The desired connection state that means the administrator holds this computer disconnected.</summary>
   private const string Disconnected = "DISCONNECTED";

   /// <summary>The status of a Windows printer that can print now.</summary>
   private const string PrinterReady = "READY";



   /// <summary>True when the server's administrator holds this computer disconnected, so only they can connect it again.</summary>
   public static bool HeldDisconnected(ServerSnapshot server)
   {
      ArgumentNullException.ThrowIfNull(server);
      return string.Equals(server.Control.DesiredState.ConnectionState, Disconnected, StringComparison.Ordinal);
   }



   /// <summary>
   /// Reads one server of a snapshot, with the computer's printers the shared ones are looked up in. When the
   /// snapshot is not <paramref name="current"/> (the service has stopped answering since it was read) the health is
   /// unknown and the sentence says so, because what the snapshot said may no longer be true.
   /// </summary>
   public static ServerStatus From(ServerSnapshot server, IReadOnlyList<ComputerPrinter> printers, bool current = true)
   {
      ArgumentNullException.ThrowIfNull(server);
      ArgumentNullException.ThrowIfNull(printers);
      string name = ShellMessages.Name(server.Name);
      if(!current)
      {
         return new ServerStatus(server.ServerId, name, HealthBadge.For(HealthLevel.Unknown), ShellMessages.NotKnownNow, ShellMessages.NotKnownNow);
      }
      HealthLevel level = HealthStatus.LevelOf(server.Health);
      string sentence = SentenceOf(server, level, printers);
      bool unauthorized = sentence is ShellMessages.AuthorizationRequired or ShellMessages.SignInRequired;
      return new ServerStatus(server.ServerId, name, HealthBadge.For(level), sentence, unauthorized ? ShellMessages.AuthorizationRequiredFor(name) : sentence);
   }



   /// <summary>The sentence beside the server's name, by the precedence the type's summary gives.</summary>
   private static string SentenceOf(ServerSnapshot server, HealthLevel level, IReadOnlyList<ComputerPrinter> printers)
   {
      if(server.Removing)
      {
         return ShellMessages.BeingRemoved;
      }
      if(server.StateUnreadable)
      {
         return ShellMessages.StateUnreadable;
      }
      if(!server.Paired)
      {
         return ShellMessages.NotPaired;
      }
      if(!server.Authentication.Authorized)
      {
         return server.Authentication.SignedIn ? ShellMessages.AuthorizationRequired : ShellMessages.SignInRequired;
      }
      if(HeldDisconnected(server))
      {
         return ShellMessages.DispatchDisconnected;
      }
      return server.Control.DesiredState.DispatchPaused ? ShellMessages.DispatchPaused : OfHealth(server, level, printers);
   }



   /// <summary>The health's own sentence.</summary>
   private static string OfHealth(ServerSnapshot server, HealthLevel level, IReadOnlyList<ComputerPrinter> printers) => level switch
   {
      HealthLevel.Green => ShellMessages.ConnectedAndReady,
      HealthLevel.Red => ShellMessages.NeedsAttention,
      HealthLevel.Orange => !string.Equals(server.Transport.State, Connected, StringComparison.Ordinal) ? ShellMessages.WaitingForConnection
         : PrintersNeedAttention(server, printers) ? ShellMessages.PrintersNeedAttention
         : ShellMessages.NotReady,
      _ => ShellMessages.HealthNotKnown,
   };



   /// <summary>True when nothing is shared with the server, or a printer shared with it is not ready or is no longer one of this computer's printers: what orange meant in 1.x.</summary>
   private static bool PrintersNeedAttention(ServerSnapshot server, IReadOnlyList<ComputerPrinter> printers)
      => server.Printers.Count == 0
         || server.Printers.Any(shared => !printers.Any(printer => string.Equals(printer.PrinterId, shared.PrinterId, StringComparison.Ordinal) && string.Equals(printer.Status, PrinterReady, StringComparison.Ordinal)));
}
