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
   public async Task ShowMessageAsync(string title, string message)
   {
      await WaitForDialogAsync();
      await inner.ShowMessageAsync(title, message);
      lifetime.ThrowIfCancellationRequested();
   }

   public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
   {
      await WaitForDialogAsync();
      bool confirmed = await inner.ConfirmAsync(title, message, confirmText, cancelText);
      lifetime.ThrowIfCancellationRequested();
      return confirmed;
   }

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
