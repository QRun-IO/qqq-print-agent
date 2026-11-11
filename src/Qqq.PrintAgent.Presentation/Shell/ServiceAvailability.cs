/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// What the last read of the snapshot says about the local service, in words for the Computer page: which case it
/// is, one sentence that says what is so, and guidance that says what to do. The cases are told apart by the typed
/// result and the snapshot's own members, never by a message's text.
/// <para>
/// A wizard can run only when the service answered and its stored data is ready (<see cref="StoredDataReady"/>).
/// While the stored data is not ready the service runs no connections and lists no servers, and the summary says
/// why: the file the upgrade from 1.x stopped at, or the version a newer build wrote and the newest this build
/// reads. A file's name is text the wire carried and is given the contract's printable form here. A refusal's
/// message already has that form (the contract's error gives it), so it is shown as it is.
/// </para>
/// </summary>
public sealed record ServiceAvailability(ServiceAvailabilityKind Kind, string Summary, string Guidance)
{
   /// <summary>The most UTF-16 units of a file's name that are shown: the longest path Windows takes without a prefix.</summary>
   private const int MaximumFileCharacters = 260;

   /// <summary>What every case in which the window goes on asking ends with.</summary>
   private const string KeepsTrying = "This window keeps trying.";



   /// <summary>Before the first read has ended: nothing is known yet.</summary>
   public static ServiceAvailability Checking { get; } = new(ServiceAvailabilityKind.Checking, "Checking the local print service…", string.Empty);



   /// <summary>True when the service answered the snapshot, whatever it said about its stored data.</summary>
   public bool Answered => Kind is ServiceAvailabilityKind.Reached or ServiceAvailabilityKind.UpdatePaused or ServiceAvailabilityKind.UpgradeIncomplete
      or ServiceAvailabilityKind.NewerBuild or ServiceAvailabilityKind.StoredDataNotReady;



   /// <summary>True when the service answered and its stored data is ready, which is when it runs connections and a wizard can start.</summary>
   public bool StoredDataReady => Kind is ServiceAvailabilityKind.Reached or ServiceAvailabilityKind.UpdatePaused;



   /// <summary>
   /// Reads the result of a snapshot. A read that was skipped, or whose wait was ended, says nothing new about the
   /// service and gives null: it is not a failure, and nothing on screen changes for it.
   /// <paramref name="serviceControlGuidance"/> is what Windows says about the service itself (stopped, not
   /// installed), when the Computer page has asked; it leads the guidance for a service that was not reached.
   /// <paramref name="shown"/> is the computer's part the window shows for a snapshot that succeeded, when that is
   /// not the snapshot's own; left out, the snapshot's own is read.
   /// </summary>
   public static ServiceAvailability? From(ServiceResult<ServiceSnapshot> result, TimestampFormat time, string? serviceControlGuidance, ComputerSnapshot? shown = null)
   {
      ArgumentNullException.ThrowIfNull(result);
      ArgumentNullException.ThrowIfNull(time);
      return result.Outcome switch
      {
         ServiceOutcome.Success => OfSnapshot(shown ?? result.Payload!.Computer, time),
         ServiceOutcome.Rejected => new ServiceAvailability(ServiceAvailabilityKind.Refused,
            "The local print service refused to report its state" + (string.IsNullOrWhiteSpace(result.Message) ? "." : ": " + result.Message), "See Logs for the reason. " + KeepsTrying),
         ServiceOutcome.Unavailable => OfFailure(result.Failure, serviceControlGuidance),
         _ => null,
      };
   }



   /// <summary>What an answered snapshot says: ready, ready with an update's pause, or which way the stored data is not ready. A status this build does not know is not ready.</summary>
   private static ServiceAvailability OfSnapshot(ComputerSnapshot computer, TimestampFormat time) => computer.State.Status switch
   {
      StoredState.Ready when computer.Update.Paused => new ServiceAvailability(ServiceAvailabilityKind.UpdatePaused, "Printing is paused while an update is installed.",
         computer.Update.PausedUntil is { } until ? $"If the update does not finish, the pause ends by itself at {time.Full(until)}." : "The pause ends when the update has finished."),
      StoredState.Ready => new ServiceAvailability(ServiceAvailabilityKind.Reached, "The local print service is running.", string.Empty),
      StoredState.UpgradeIncomplete => new ServiceAvailability(ServiceAvailabilityKind.UpgradeIncomplete,
         File(computer.State.File) is { } file ? $"The upgrade from an earlier version stopped at the file {file}, so the service is not printing."
            : "The upgrade from an earlier version did not finish, so the service is not printing.",
         "The service tries the upgrade again each time it starts: restart the service from this page. If it stops again, see Logs for the reason. No server can be added until the upgrade has finished."),
      StoredState.NewerBuild => new ServiceAvailability(ServiceAvailabilityKind.NewerBuild,
         computer.State is { FoundVersion: { } found, SupportedVersion: { } supported }
            ? string.Create(CultureInfo.InvariantCulture, $"A newer build of the print agent wrote the stored data (version {found}; this build reads up to version {supported}), so the service is not printing.")
            : "A newer build of the print agent wrote the stored data, so the service is not printing.",
         "Install that newer build again; this build never changes data a newer one wrote. Updates are in Settings."),
      _ => new ServiceAvailability(ServiceAvailabilityKind.StoredDataNotReady,
         "The service reports that its stored data is not ready, in a way this version of the companion does not know, so the service is not printing.",
         "Update the companion to the service's version. Updates are in Settings."),
   };



   /// <summary>What a read that brought no usable answer says, by its cause.</summary>
   private static ServiceAvailability OfFailure(ServiceFailure failure, string? serviceControlGuidance) => failure switch
   {
      ServiceFailure.OlderService => new ServiceAvailability(ServiceAvailabilityKind.OlderService, "The Windows service is older than this companion and does not know its requests.",
         "Finish the update: install the service in the same version as this companion. " + KeepsTrying),
      ServiceFailure.NewerService => new ServiceAvailability(ServiceAvailabilityKind.NewerService, "The Windows service is newer than this companion, which cannot read its replies.",
         "Update the companion to the service's version. Updates are in Settings."),
      ServiceFailure.UnreadableReply => new ServiceAvailability(ServiceAvailabilityKind.Unreadable, "The local print service answered, and the answer could not be read.",
         "Install matching versions of the companion and the service. " + KeepsTrying),
      _ => new ServiceAvailability(ServiceAvailabilityKind.NotReached, "The local print service could not be reached.",
         (string.IsNullOrWhiteSpace(serviceControlGuidance) ? string.Empty : serviceControlGuidance.Trim() + " ")
         + "Use the service controls on this page, Windows Services or the installer. This window needs administrator access. " + KeepsTrying
         + " What it shows of the servers may be out of date until the service answers."),
   };



   /// <summary>A file's name as it is shown, or null when the service named none or the name shows nothing.</summary>
   private static string? File(string? file)
   {
      string shown = ManagementText.Bounded(file ?? string.Empty, MaximumFileCharacters).Trim();
      return shown.Length == 0 ? null : shown;
   }
}
