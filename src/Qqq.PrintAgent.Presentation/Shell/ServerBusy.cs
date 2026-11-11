/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using Qqq.PrintAgent.Presentation.Service;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// Which servers are busy, and which have been changed from this window and when. It stands on the one
/// <see cref="OperationGate"/> the companion has: a command enters the gate under its server's identifier (or
/// <see cref="OperationGate.Computer"/> for a change to the computer or to the list of servers) and holds the key
/// until it has ended, and a second command on a key that is held is refused with the holder's sentence. A command
/// on another key is not affected, and the reads the window repeats never enter the gate at all.
/// <para>
/// It also keeps one stamp per key of when that key was last changed locally, which replaces the one counter the
/// 1.x window had for everything. A stamp is a count of the snapshots the client had handed to the pipe, taken from
/// the client and from nowhere else, so it can be compared with the number a snapshot's result carries
/// (<see cref="ServiceResult{T}.Flight"/>): what a snapshot says about a key is shown only when its number is
/// strictly greater than the key's stamp (<see cref="ChangedSince"/>). Such a snapshot was handed to the pipe
/// after the stamp was taken, whoever asked for it; any other may have been sent before the change was carried
/// out, and showing it would put back what the operator has just changed.
/// </para>
/// <para>
/// A stamp is always taken after the change it marks was answered, never before it was sent: a count read before
/// would let through a snapshot that was sent before the change was carried out. A command changes its key when it
/// ends, whoever entered the gate: its hold is given back after its answer has arrived, and the stamp is the
/// client's count read then. It also changes its key when it starts, with the count read then, which holds back
/// what was sent before the command began and nothing that was sent while it runs, so a server that is busy for
/// minutes keeps refreshing. <see cref="NoteChange"/> records a change with the count its own result carries,
/// which was read when its reply was read: that is how a change made without the gate is recorded, and how a
/// command that goes on waiting before it gives its hold back marks its key at the moment of its answer. The
/// counts are one client's and start again with the companion, and so do the stamps.
/// </para>
/// </summary>
public sealed class ServerBusy : IDisposable
{
   /// <summary>The gate every command enters.</summary>
   private readonly OperationGate _gate;

   /// <summary>The client whose count of snapshots the stamps are read from, which is the client the snapshots come from.</summary>
   private readonly ManagementClient _client;

   /// <summary>Guards the stamps; a command's hold may be released on another thread than the one that took it.</summary>
   private readonly Lock _sync = new();

   /// <summary>For each key that was changed locally, the stamp of its last change: how many snapshots the client had handed to the pipe when the change was known to have been answered.</summary>
   private readonly Dictionary<string, long> _changed = new(StringComparer.Ordinal);



   /// <summary>Stands on a gate and follows every command that enters and leaves it. The client is the one whose snapshots are compared with the stamps.</summary>
   public ServerBusy(OperationGate gate, ManagementClient client)
   {
      _gate = gate ?? throw new ArgumentNullException(nameof(gate));
      _client = client ?? throw new ArgumentNullException(nameof(client));
      _gate.Changed += OnGateChanged;
   }



   /// <summary>True while a command holds the key.</summary>
   public bool IsBusy(string key) => _gate.IsBusy(key);



   /// <summary>
   /// Starts a command under a key. When the key is free the command holds it until <paramref name="hold"/> is
   /// disposed, and <paramref name="whileBusy"/> is the sentence a second command on the key is refused with
   /// meanwhile. When the key is held the command must not start, and <paramref name="refusal"/> is the holder's
   /// sentence, for the caller to show: the second command is never dropped without a word and never queued. The
   /// hold is given back when the command's answer has arrived, not before: giving it back is what marks the key
   /// as changed.
   /// </summary>
   public bool TryBegin(string key, string whileBusy, [NotNullWhen(true)] out IDisposable? hold, [NotNullWhen(false)] out string? refusal)
      => _gate.TryEnter(key, whileBusy, out hold, out refusal);



   /// <summary>
   /// True when what a snapshot says about the key is not to be shown: the key was changed locally, and the
   /// snapshot's request is not known to have been handed to the pipe after that change was answered.
   /// <paramref name="flight"/> is the number the snapshot's result carries. Only a number strictly greater than the
   /// key's stamp is newer than the change: the snapshot with the stamp's own number was already on its way when
   /// the stamp was taken. A result that names no flight is never newer. A key that was never changed locally is
   /// held back by nothing.
   /// </summary>
   public bool ChangedSince(string key, long? flight)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(key);
      lock(_sync)
      {
         return _changed.TryGetValue(key, out long stamp) && (flight is not { } number || number <= stamp);
      }
   }



   /// <summary>
   /// Records that something under the key was changed from this window, by a request whose result is
   /// <paramref name="changed"/>: the result of the change itself, as the client returned it. The key is stamped
   /// with the count that result carries (<see cref="ServiceResult{T}.SnapshotsSent"/>), which the client read when
   /// the reply was read, so the stamp cannot be from before the change was answered. A result of a change whose
   /// outcome is not known is passed in the same way: it carries the count read when the failure, or the end of
   /// the wait, was known, and the key is held back until a snapshot that was sent after that. A result that
   /// carries no count is that of a request that was never handed to the pipe, and marks nothing. A stamp never
   /// goes back: a result that is noted late does not undo a later change's stamp.
   /// </summary>
   public void NoteChange<T>(string key, ServiceResult<T> changed)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(key);
      ArgumentNullException.ThrowIfNull(changed);
      if(changed.SnapshotsSent is { } snapshotsSent)
      {
         Stamp(key, snapshotsSent);
      }
   }



   /// <summary>Stops following the gate.</summary>
   public void Dispose() => _gate.Changed -= OnGateChanged;



   /// <summary>A command started or ended under a key: either way the key has just changed, and is stamped with the client's count as it is now.</summary>
   private void OnGateChanged(object? sender, OperationGateChangedEventArgs e) => Stamp(e.Key, _client.SnapshotsSent);



   /// <summary>Stamps a key, unless it already has a later stamp.</summary>
   private void Stamp(string key, long snapshotsSent)
   {
      lock(_sync)
      {
         _changed[key] = Math.Max(snapshotsSent, _changed.GetValueOrDefault(key, snapshotsSent));
      }
   }
}
