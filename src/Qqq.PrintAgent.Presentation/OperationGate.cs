/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;

namespace Qqq.PrintAgent.Presentation;

/// <summary>
/// One gate per key, so that one command at a time runs on each thing the operator can change: a key is a server's
/// identifier, <see cref="Computer"/> or <see cref="Wizard"/>. A command enters the gate under its key before it
/// sends anything and holds the key until it has ended. A second command on a key that is held is refused, and the
/// refusal is a sentence for the operator: the one the command that holds the key gave when it entered, which says
/// what is still running and what to do. The second command is never dropped without a word and never queued to run
/// later, when the operator may no longer want it (ADR-0005, "One status at a time, per place"). A command on another
/// key is not affected, so a sign-in on one server leaves every other server usable.
/// <para>
/// Reads that are repeated on a timer (the snapshot, the transport read, a page of jobs) do not enter the gate:
/// refresh never stops while a command runs.
/// </para>
/// <para>
/// The gate keeps no sentences of its own and writes nothing to the log; the caller shows the refusal and logs it.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised on the thread that entered or released the key, after the key's state has
/// changed and outside the gate's lock. That thread need not be the window's: a hold can be released wherever the
/// command that held it ended. And what the event says can be out of date by the time a listener hears it: another
/// thread, or a listener earlier in the list that entered or released the key from inside the event, may have
/// changed the key again, and two events from two threads can arrive in either order. So a listener does not keep
/// <see cref="OperationGateChangedEventArgs.Busy"/> as the key's state. It takes the event as "this key changed",
/// goes to its own thread, and reads <see cref="IsBusy"/> there.
/// </para>
/// </summary>
public sealed class OperationGate
{
   /// <summary>The key for commands that change the computer and not one server.</summary>
   public const string Computer = "computer";



   /// <summary>The key for the connection wizard's own commands. A server's identifier is never this text or <see cref="Computer"/>.</summary>
   public const string Wizard = "wizard";



   /// <summary>Guards <see cref="_held"/>. A command is entered and released on its owner's thread, but a release that arrives on another thread must not corrupt the table.</summary>
   private readonly Lock _sync = new();



   /// <summary>The keys that are held, each with the sentence a second command on it is refused with.</summary>
   private readonly Dictionary<string, string> _held = new(StringComparer.Ordinal);



   /// <summary>
   /// Raised when a key becomes busy and when it becomes free again, on the thread that entered or released it. A
   /// refused command raises nothing, because nothing changed. A listener reads <see cref="IsBusy"/> for the key on
   /// its own thread; the state in the arguments can be out of date.
   /// </summary>
   public event EventHandler<OperationGateChangedEventArgs>? Changed;



   /// <summary>True while a command holds the key.</summary>
   public bool IsBusy(string key)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(key);
      lock(_sync)
      {
         return _held.ContainsKey(key);
      }
   }



   /// <summary>
   /// Enters the gate for one command. When the key is free the command holds it from now until
   /// <paramref name="hold"/> is disposed, and <paramref name="whileBusy"/> is the sentence any other command on the
   /// key is refused with meanwhile. When the key is held the command must not start: <paramref name="refusal"/> is
   /// the holder's sentence, for the caller to show. Disposing a hold a second time does nothing, so it can never
   /// release a later command's hold on the same key.
   /// </summary>
   public bool TryEnter(string key, string whileBusy, [NotNullWhen(true)] out IDisposable? hold, [NotNullWhen(false)] out string? refusal)
   {
      ArgumentException.ThrowIfNullOrWhiteSpace(key);
      ArgumentException.ThrowIfNullOrWhiteSpace(whileBusy);
      lock(_sync)
      {
         if(_held.TryGetValue(key, out refusal))
         {
            hold = null;
            return false;
         }
         _held.Add(key, whileBusy);
      }
      hold = new Hold(this, key);
      Changed?.Invoke(this, new OperationGateChangedEventArgs(key, busy: true));
      return true;
   }



   /// <summary>Frees a key. Only the hold that was given for it calls this, and only once.</summary>
   private void Leave(string key)
   {
      lock(_sync)
      {
         _held.Remove(key);
      }
      Changed?.Invoke(this, new OperationGateChangedEventArgs(key, busy: false));
   }



   /// <summary>A command's hold on its key. Disposing it frees the key; disposing it again does nothing.</summary>
   private sealed class Hold : IDisposable
   {
      /// <summary>The gate the key is held in.</summary>
      private readonly OperationGate _gate;

      /// <summary>The key that is held.</summary>
      private readonly string _key;

      /// <summary>0 until the hold is released, then 1.</summary>
      private int _released;



      /// <summary>Records what to free.</summary>
      internal Hold(OperationGate gate, string key)
      {
         _gate = gate;
         _key = key;
      }



      /// <summary>Frees the key, once.</summary>
      public void Dispose()
      {
         if(Interlocked.Exchange(ref _released, 1) == 0)
         {
            _gate.Leave(_key);
         }
      }
   }
}
