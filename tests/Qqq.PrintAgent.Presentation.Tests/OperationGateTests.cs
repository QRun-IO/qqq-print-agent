/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Tests;

/// <summary>One command at a time per key: a second one is refused with the holder's sentence, is not queued, and leaves every other key alone.</summary>
public sealed class OperationGateTests
{
   private const string ServerOne = "3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f";

   private const string ServerTwo = "7a1c9e2d-0b3f-4c5a-8d6e-1f2a3b4c5d6e";



   /// <summary>A free key is taken by the first command and is busy until that command's hold is released.</summary>
   [Fact]
   public void Enter_AFreeKey_IsHeldUntilTheHoldIsReleased()
   {
      OperationGate gate = new();
      Assert.False(gate.IsBusy(ServerOne));

      Assert.True(gate.TryEnter(ServerOne, "Signing in is still running.", out IDisposable? hold, out string? refusal));

      Assert.NotNull(hold);
      Assert.Null(refusal);
      Assert.True(gate.IsBusy(ServerOne));
      hold.Dispose();
      Assert.False(gate.IsBusy(ServerOne));
   }



   /// <summary>
   /// A second command on a busy key is refused with the sentence the first one gave, not with its own, and gets no
   /// hold. It is not queued: when the first ends the key is simply free, and the second runs only if it is asked for
   /// again.
   /// </summary>
   [Fact]
   public void SecondCommandOnABusyKey_IsRefusedWithTheHoldersSentence_AndIsNotQueued()
   {
      OperationGate gate = new();
      Assert.True(gate.TryEnter(ServerOne, "Signing in is still running. Wait for it to finish.", out IDisposable? first, out _));

      Assert.False(gate.TryEnter(ServerOne, "Removing is still running.", out IDisposable? second, out string? refusal));
      Assert.False(gate.TryEnter(ServerOne, "Renaming is still running.", out IDisposable? third, out string? again));

      Assert.Null(second);
      Assert.Null(third);
      Assert.Equal("Signing in is still running. Wait for it to finish.", refusal);
      Assert.Equal("Signing in is still running. Wait for it to finish.", again);
      first.Dispose();
      Assert.False(gate.IsBusy(ServerOne));
      Assert.True(gate.TryEnter(ServerOne, "Removing is still running.", out IDisposable? asked, out _));
      Assert.False(gate.TryEnter(ServerOne, "Renaming is still running.", out _, out string? now));
      Assert.Equal("Removing is still running.", now);
      asked.Dispose();
   }



   /// <summary>A command on one server leaves another server, the computer and the wizard free, and each key is refused with its own holder's sentence.</summary>
   [Fact]
   public void BusyOnOneKey_LeavesEveryOtherKeyFree()
   {
      OperationGate gate = new();
      Assert.True(gate.TryEnter(ServerOne, "One is busy.", out IDisposable? one, out _));

      Assert.False(gate.IsBusy(ServerTwo));
      Assert.False(gate.IsBusy(OperationGate.Computer));
      Assert.False(gate.IsBusy(OperationGate.Wizard));
      Assert.True(gate.TryEnter(ServerTwo, "Two is busy.", out IDisposable? two, out _));
      Assert.True(gate.TryEnter(OperationGate.Computer, "The computer is busy.", out IDisposable? computer, out _));
      Assert.True(gate.TryEnter(OperationGate.Wizard, "The wizard is busy.", out IDisposable? wizard, out _));
      Assert.False(gate.TryEnter(ServerTwo, "x", out _, out string? refusedTwo));
      Assert.False(gate.TryEnter(OperationGate.Wizard, "x", out _, out string? refusedWizard));
      Assert.Equal("Two is busy.", refusedTwo);
      Assert.Equal("The wizard is busy.", refusedWizard);

      two.Dispose();
      Assert.True(gate.IsBusy(ServerOne));
      Assert.False(gate.IsBusy(ServerTwo));
      Assert.True(gate.IsBusy(OperationGate.Computer));
      one.Dispose();
      computer.Dispose();
      wizard.Dispose();
      Assert.False(gate.IsBusy(OperationGate.Computer));
      Assert.False(gate.IsBusy(OperationGate.Wizard));
   }



   /// <summary>A key is compared exactly: another letter case is another key, as a server identifier has one spelling.</summary>
   [Fact]
   public void Keys_AreComparedExactly()
   {
      OperationGate gate = new();
      Assert.True(gate.TryEnter("wizard", "Busy.", out IDisposable? hold, out _));

      Assert.False(gate.IsBusy("Wizard"));
      Assert.True(gate.IsBusy(OperationGate.Wizard));
      hold.Dispose();
   }



   /// <summary>A hold released a second time does nothing: it cannot free the key for the command that took it afterwards.</summary>
   [Fact]
   public void ReleasingAHoldTwice_DoesNotReleaseALaterCommand()
   {
      OperationGate gate = new();
      List<string> changes = [];
      gate.Changed += (_, change) => changes.Add(change.Key + (change.Busy ? " busy" : " free"));
      Assert.True(gate.TryEnter(ServerOne, "First.", out IDisposable? first, out _));
      first.Dispose();
      Assert.True(gate.TryEnter(ServerOne, "Second.", out IDisposable? second, out _));

      first.Dispose();

      Assert.True(gate.IsBusy(ServerOne));
      Assert.False(gate.TryEnter(ServerOne, "Third.", out _, out string? refusal));
      Assert.Equal("Second.", refusal);
      second.Dispose();
      Assert.Equal([ServerOne + " busy", ServerOne + " free", ServerOne + " busy", ServerOne + " free"], changes);
   }



   /// <summary>The change is announced once when a key becomes busy and once when it becomes free, with the key, on the calling thread and after the state is the new one. A refused command announces nothing.</summary>
   [Fact]
   public void Changed_SaysWhichKeyBecameBusyOrFree_AndNothingForARefusal()
   {
      OperationGate gate = new();
      List<(string Key, bool Busy, bool StateThen, int Thread)> changes = [];
      gate.Changed += (sender, change) =>
      {
         Assert.Same(gate, sender);
         changes.Add((change.Key, change.Busy, gate.IsBusy(change.Key), Environment.CurrentManagedThreadId));
      };
      int here = Environment.CurrentManagedThreadId;

      Assert.True(gate.TryEnter(ServerOne, "Busy.", out IDisposable? hold, out _));
      Assert.False(gate.TryEnter(ServerOne, "Other.", out _, out _));
      hold.Dispose();

      Assert.Equal([(ServerOne, true, true, here), (ServerOne, false, false, here)], changes);
   }



   /// <summary>Nobody has to listen for changes.</summary>
   [Fact]
   public void EnterAndRelease_WithNoSubscriber_Work()
   {
      OperationGate gate = new();

      Assert.True(gate.TryEnter(OperationGate.Computer, "Busy.", out IDisposable? hold, out _));
      hold.Dispose();

      Assert.False(gate.IsBusy(OperationGate.Computer));
   }



   /// <summary>Asking whether a key is busy, which is all a repeated read ever does with the gate, takes nothing: the key stays free for a command.</summary>
   [Fact]
   public void AskingWhetherAKeyIsBusy_DoesNotHoldIt()
   {
      OperationGate gate = new();

      Assert.False(gate.IsBusy(ServerOne));
      Assert.False(gate.IsBusy(ServerOne));

      Assert.True(gate.TryEnter(ServerOne, "Busy.", out IDisposable? hold, out _));
      hold.Dispose();
   }



   /// <summary>A key and a sentence must both say something; a command with neither is a mistake in the caller, and nothing is held for it.</summary>
   [Theory]
   [InlineData("")]
   [InlineData("  ")]
   public void BlankKeyOrSentence_IsRefusedAsAnArgument(string blank)
   {
      OperationGate gate = new();

      Assert.Equal("key", Assert.Throws<ArgumentException>(() => gate.TryEnter(blank, "Busy.", out _, out _)).ParamName);
      Assert.Equal("key", Assert.Throws<ArgumentNullException>(() => gate.TryEnter(null!, "Busy.", out _, out _)).ParamName);
      Assert.Equal("whileBusy", Assert.Throws<ArgumentException>(() => gate.TryEnter(ServerOne, blank, out _, out _)).ParamName);
      Assert.Equal("whileBusy", Assert.Throws<ArgumentNullException>(() => gate.TryEnter(ServerOne, null!, out _, out _)).ParamName);
      Assert.Equal("key", Assert.Throws<ArgumentException>(() => gate.IsBusy(blank)).ParamName);
      Assert.Equal("key", Assert.Throws<ArgumentNullException>(() => gate.IsBusy(null!)).ParamName);
      Assert.False(gate.IsBusy(ServerOne));
   }



   /// <summary>The two keys that are not servers are fixed text, and neither can be a server identifier.</summary>
   [Fact]
   public void ComputerAndWizardKeys_AreNotServerIdentifiers()
   {
      Assert.Equal("computer", OperationGate.Computer);
      Assert.Equal("wizard", OperationGate.Wizard);
      Assert.False(Guid.TryParse(OperationGate.Computer, out _));
      Assert.False(Guid.TryParse(OperationGate.Wizard, out _));
   }
}
