/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Interaction;
using KofTwentyTwo.AppKit.WinUI;


namespace Qqq.PrintAgent;

/// <summary>Defers shared AppKit prompts until other dialogs close and stops them with the window.</summary>
internal sealed class LifetimeUserPrompter(IUserPrompter inner, CancellationToken lifetime) : IUserPrompter
{
   /// <summary>Shows an informational message once no other dialog is open, and fails with a cancellation if the window closed meanwhile, so no prompt appears after shutdown.</summary>
   public async Task ShowMessageAsync(string title, string message)
   {
      await WaitForDialogAsync();
      await inner.ShowMessageAsync(title, message);
      lifetime.ThrowIfCancellationRequested();
   }



   /// <summary>Asks for confirmation once no other dialog is open; an answer that arrives after the window closed is discarded by cancelling, so a late click cannot approve anything.</summary>
   public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
   {
      await WaitForDialogAsync();
      bool confirmed = await inner.ConfirmAsync(title, message, confirmText, cancelText);
      lifetime.ThrowIfCancellationRequested();
      return confirmed;
   }



   /// <summary>Waits, checking every 150 ms, until the shared dialog guard reports no dialog open. It stays on the UI context so the check and the opening of the next dialog cannot interleave with other UI commands, and it stops as soon as the window closes.</summary>
   private async Task WaitForDialogAsync()
   {
      lifetime.ThrowIfCancellationRequested();
      while(DialogGuard.IsDialogOpen)
      {
         // Keep the UI synchronization context so checking the guard and opening
         // the shared dialog remain serialized with other UI commands.
         await Task.Delay(150, lifetime);
      }
      lifetime.ThrowIfCancellationRequested();
   }
}
