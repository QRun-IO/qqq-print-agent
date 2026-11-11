/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// The two reads the window repeats: the snapshot and the transport meter. The window's timers call
/// <see cref="RefreshAsync"/> and <see cref="RefreshTransportAsync"/>; the shell has no timer and no clock of its
/// own, so a test ticks it by calling them. Each read is one request for the whole computer, however many servers
/// it has. A tick that arrives while the read it would start is still in flight starts nothing, so neither read ever
/// overlaps itself and a slow service cannot queue requests up; the two reads are independent of each other, so a
/// slow snapshot does not stop the meter. Neither read enters the gate: they go on while a command runs on any
/// server.
/// </summary>
public sealed partial class ShellViewModel
{
   /// <summary>The transport state of each server, from the last read of the meter that succeeded; empty after one that failed.</summary>
   private Dictionary<string, ServerTransport> _transports = new(StringComparer.Ordinal);

   /// <summary>The snapshot read that was started last; null before the first.</summary>
   private Task? _refresh;

   /// <summary>The transport read that was started last; null before the first.</summary>
   private Task? _transportRefresh;

   /// <summary>True from the moment a snapshot read starts until what it brought has been shown.</summary>
   private bool _refreshing;

   /// <summary>True from the moment a transport read starts until what it brought has been taken.</summary>
   private bool _refreshingTransport;

   /// <summary>True while the log has been told that the snapshot lists servers the window cannot show.</summary>
   private bool _unusableNoted;



   /// <summary>
   /// Reads the snapshot and shows it. What it says about a server that was changed from this window after the
   /// read was asked for is not shown; what it says about every other server is. A read that was skipped because
   /// the client had too many requests in flight is not a failure and changes nothing. Called while a read it
   /// started is still in flight, it starts nothing and returns that read, so whoever awaits it waits for the
   /// snapshot that is on its way.
   /// </summary>
   public Task RefreshAsync()
   {
      if(_disposed)
      {
         return Task.CompletedTask;
      }
      return _refreshing ? _refresh ?? Task.CompletedTask : _refresh = ReadSnapshotAsync();
   }



   /// <summary>
   /// Reads a snapshot that is newer than this call and shows it. A page calls this when a command of its own has
   /// ended, so the window shows what the service says after the change and not what a read that was already on
   /// its way says. A read the shell has in flight was sent before the call: it is waited for (what it says about a
   /// server that changed since it was asked for is not shown), and only then is a new read made. Called while a
   /// snapshot is being shown, it starts nothing, as <see cref="RefreshAsync"/> does.
   /// </summary>
   public async Task RefreshAfterChangeAsync()
   {
      while(_refreshing && _refresh is { IsCompleted: false } sentEarlier)
      {
         await sentEarlier;
      }
      if(!_disposed && !_refreshing)
      {
         await (_refresh = ReadSnapshotAsync(newerThanTheCall: true));
      }
   }



   /// <summary>
   /// Reads every server's transport state and traffic meter. After a read that succeeded,
   /// <see cref="TransportOf"/> gives each server's; after one that failed it gives nothing, so a page says that the
   /// meter is unavailable and does not go on showing old figures. A read that was skipped changes nothing. Called
   /// while a read it started is still in flight, it starts nothing and returns that read.
   /// </summary>
   public Task RefreshTransportAsync()
   {
      if(_disposed)
      {
         return Task.CompletedTask;
      }
      return _refreshingTransport ? _transportRefresh ?? Task.CompletedTask : _transportRefresh = ReadTransportAsync();
   }



   /// <summary>
   /// One read of the snapshot. The mark of the moment the read is asked for is taken here, in the one place a
   /// snapshot is asked for, and goes with the result to where it is shown. The mark is right when this call is what
   /// sends the request, which it is as long as the shell is the only one that asks the client for snapshots: the
   /// client shares one request among everyone who asks while it is in flight, and it does not say when the request
   /// a caller was handed had been sent. A read that has to be newer than the call that asks for it goes through
   /// <see cref="SnapshotSentAfterTheCallAsync"/>.
   /// </summary>
   private async Task ReadSnapshotAsync(bool newerThanTheCall = false)
   {
      _refreshing = true;
      try
      {
         long asked = Busy.Mark();
         ServiceResult<ServiceSnapshot> result = await (newerThanTheCall ? SnapshotSentAfterTheCallAsync() : _client.SnapshotAsync());
         if(!_disposed)
         {
            Apply(result, asked);
         }
      }
      finally
      {
         _refreshing = false;
      }
   }



   /// <summary>
   /// Asks the client for a snapshot whose request is sent after this call is made, never one that was already in
   /// flight. The client gets exactly that in the fix round of its review, as
   /// <c>SnapshotAsync(sentAfterTheCall: true)</c>, which waits its turn and is never skipped. This branch stands on
   /// the client as it was before that round, so today this is the read the timer makes, and what makes it newer
   /// than the call is that <see cref="RefreshAfterChangeAsync"/> has first waited for the shell's own read in
   /// flight to end. Today it can therefore still be skipped when the client has too many requests in flight, and
   /// the next tick then shows the change. When this branch is rebased onto the client's fix round, the body of
   /// this method becomes <c>_client.SnapshotAsync(sentAfterTheCall: true)</c> and nothing else changes.
   /// </summary>
   private Task<ServiceResult<ServiceSnapshot>> SnapshotSentAfterTheCallAsync() => _client.SnapshotAsync();



   /// <summary>One read of the transport meter.</summary>
   private async Task ReadTransportAsync()
   {
      _refreshingTransport = true;
      try
      {
         ServiceResult<TransportResult> result = await _client.TransportAsync();
         if(_disposed || result.Outcome is ServiceOutcome.Skipped or ServiceOutcome.Canceled)
         {
            return;
         }
         _transports = result.Succeeded
            ? result.Payload.Servers.DistinctBy(server => server.ServerId, StringComparer.Ordinal).ToDictionary(server => server.ServerId, StringComparer.Ordinal)
            : new Dictionary<string, ServerTransport>(StringComparer.Ordinal);
         TransportChanged?.Invoke(this, EventArgs.Empty);
      }
      finally
      {
         _refreshingTransport = false;
      }
   }



   /// <summary>A server's transport state, traffic meter and last contact as the last read of the meter gave them, or null when that read failed, has not happened yet or did not list the server.</summary>
   public ServerTransport? TransportOf(string serverId) => _transports.GetValueOrDefault(serverId);



   /// <summary>Takes the result of a snapshot read that was asked for at a mark. What is said of the service comes from the computer's part that is shown, which is the window's own when the computer was changed from this window after the read was asked for.</summary>
   private void Apply(ServiceResult<ServiceSnapshot> result, long asked)
   {
      if(result.Succeeded)
      {
         NoteUnusable(result.Payload.Servers);
         Snapshot = SnapshotMerge.Apply(Snapshot, result.Payload, key => Busy.ChangedSince(key, asked));
      }
      if(ServiceAvailability.From(result, _time, ServiceControlGuidance, Snapshot?.Computer) is not { } availability)
      {
         return;
      }
      _current = result.Succeeded;
      if(availability.Kind != Availability.Kind)
      {
         Log(availability.Kind);
      }
      Availability = availability;
      Render();
      SnapshotApplied?.Invoke(this, EventArgs.Empty);
   }



   /// <summary>
   /// Makes everything follow the snapshot and what is known of the service: the tree, the footer and the tray, then
   /// which page is on screen, and last the footer's announcement, once, when its sentence changed. By then the
   /// footer holds the sentence that is announced.
   /// </summary>
   private void Render()
   {
      IReadOnlyList<ServerSnapshot> servers = Snapshot?.Servers ?? [];
      IReadOnlyList<ComputerPrinter> printers = Snapshot?.Computer.Printers ?? [];
      ServerNode? went = Tree.Reconcile(servers, printers, _current, Busy.IsBusy, Availability.StoredDataReady ? PageKey.Wizard : PageKey.Computer);
      Tree.AddServer.Set(ReasonNotToAdd(), WizardOpen);
      IReadOnlyList<ServerStatus> statuses = [.. servers.Select(server => ServerStatus.From(server, printers, _current))];
      Footer = FooterStatus.From(Availability, statuses);
      Tray = TrayStatus.From(Availability, Footer, statuses, _productName);
      Place(went);
      CurrentPage = ShellMessages.Heading(Selected, Tree.Server(Selected.ServerId)?.Name);
      if(!string.Equals(Footer.Text, FooterAnnouncement.Text, StringComparison.Ordinal))
      {
         FooterAnnouncement.Announce(Footer.Text);
      }
   }



   /// <summary>
   /// Decides which page is on screen after a reconcile. When the selected server went, the tree has already moved
   /// to its neighbour, and a message there says that the server went (not on the wizard's page, which shows no
   /// message from elsewhere). A page the wizard asked for is shown once its server is listed. Otherwise, the first
   /// time the service answers with its stored data ready and the operator has not chosen a page, the window lands.
   /// </summary>
   private void Place(ServerNode? went)
   {
      if(went is not null)
      {
         Messages.Forget(went.ServerId);
         _log.Info("Server {ServerId} is no longer on this computer; its page was on screen, and {Page} is shown instead.", went.ServerId, Selected);
         GoTo(Selected);
         if(Selected != PageKey.Wizard)
         {
            Messages.Show(Selected, ShellMessages.ServerRemoved(went.Name));
         }
      }
      else if(_awaited is { } awaited && Show(awaited))
      {
         _awaited = null;
         _operatorChose = true;
      }
      else if(!_landed && !_operatorChose && Availability.StoredDataReady)
      {
         _landed = true;
         GoTo(Landing());
      }
   }



   /// <summary>Says once in the log, for as long as it lasts, that the snapshot lists servers the window cannot show.</summary>
   private void NoteUnusable(IReadOnlyList<ServerSnapshot> servers)
   {
      int unusable = servers.Count - SnapshotMerge.Usable(servers).Count;
      if(unusable > 0 && !_unusableNoted)
      {
         _log.Warning("The snapshot lists {Count} servers this window cannot show: their identifier is not a server identifier, or another server has the same one.", unusable);
      }
      _unusableNoted = unusable > 0;
   }



   /// <summary>Says in the log what is now known of the service: a warning while it is not answering or not printing, and otherwise a line of normal operation.</summary>
   private void Log(ServiceAvailabilityKind kind)
   {
      if(kind is ServiceAvailabilityKind.Reached or ServiceAvailabilityKind.UpdatePaused)
      {
         _log.Info("The local print service answered a snapshot ({Availability}).", kind);
      }
      else
      {
         _log.Warning("The local print service is not available to this window ({Availability}).", kind);
      }
   }
}
