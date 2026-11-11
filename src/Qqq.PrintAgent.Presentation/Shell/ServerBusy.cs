/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// Which servers are busy, and which have been changed from this window and when. It stands on the one
/// <see cref="OperationGate"/> the companion has: a command enters the gate under its server's identifier (or
/// <see cref="OperationGate.Computer"/> for a change to the computer or to the list of servers) and holds the key
/// until it has ended, and a second command on a key that is held is refused with the holder's sentence. A command
/// on another key is not affected, and the reads the window repeats never enter the gate at all.
/// <para>
/// It also keeps one mark per key of when that key was last changed locally, which replaces the one counter the 1.x
/// window had for everything. A command changes its key when it starts and again when it ends, whoever entered the
/// gate; <see cref="NoteChange"/> records a change that was made without the gate. A snapshot remembers the mark of
/// the moment it was asked for (<see cref="Mark"/>), and what it says about a server is not shown when that server
/// has changed since (<see cref="ChangedSince"/>): the snapshot is older than the change, and showing it would put
/// back what the operator has just changed.
/// </para>
/// </summary>
public sealed class ServerBusy : IDisposable
{
   /// <summary>The gate every command enters.</summary>
   private readonly OperationGate _gate;

   /// <summary>Guards the marks; a command's hold may be released on another thread than the one that took it.</summary>
   private readonly Lock _sync = new();

   /// <summary>For each key, the mark of its last local change.</summary>
   private readonly Dictionary<string, long> _changed = new(StringComparer.Ordinal);

   /// <summary>The mark of the last local change to any key.</summary>
   private long _mark;



   /// <summary>Stands on a gate and follows every command that enters and leaves it.</summary>
   public ServerBusy(OperationGate gate)
   {
      _gate = gate ?? throw new ArgumentNullException(nameof(gate));
      _gate.Changed += OnGateChanged;
   }



   /// <summary>True while a command holds the key.</summary>
   public bool IsBusy(string key) => _gate.IsBusy(key);



   /// <summary>
   /// Starts a command under a key. When the key is free the command holds it until <paramref name="hold"/> is
   /// disposed, and <paramref name="whileBusy"/> is the sentence a second command on the key is refused with
   /// meanwhile. When the key is held the command must not start, and <paramref name="refusal"/> is the holder's
   /// sentence, for the caller to show: the second command is never dropped without a word and never queued.
   /// </summary>
   public bool TryBegin(string key, string whileBusy, [NotNullWhen(true)] out IDisposable? hold, [NotNullWhen(false)] out string? refusal)
      => _gate.TryEnter(key, whileBusy, out hold, out refusal);



   /// <summary>The mark of this moment. A read remembers it when it is asked for.</summary>
   public long Mark()
   {
      lock(_sync)
      {
         return _mark;
      }
   }



   /// <summary>True when the key has been changed locally since the mark was taken.</summary>
   public bool ChangedSince(string key, long mark)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(key);
      lock(_sync)
      {
         return _changed.TryGetValue(key, out long changed) && changed > mark;
      }
   }



   /// <summary>Records that something under the key was changed from this window just now.</summary>
   public void NoteChange(string key)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(key);
      lock(_sync)
      {
         _changed[key] = ++_mark;
      }
   }



   /// <summary>Stops following the gate.</summary>
   public void Dispose() => _gate.Changed -= OnGateChanged;



   /// <summary>A command started or ended under a key: either way the key has just changed.</summary>
   private void OnGateChanged(object? sender, OperationGateChangedEventArgs e) => NoteChange(e.Key);
}
