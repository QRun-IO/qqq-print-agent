/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Principal;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The two members of the real task scheduler adapter that only read: a path's attributes, in a temporary folder, and the
/// identifier an account name stands for, for accounts every Windows has. Nothing here starts the scheduler's program or
/// touches this computer's scheduled tasks.
/// </summary>
public sealed class WindowsTaskSchedulerTests
{
   /// <summary>A folder reports as a folder, a file as a file, neither as a link, and a path where nothing exists reports nothing.</summary>
   [Fact]
   public void AttributesAreReadForWhatExistsAndNothingIsReportedForWhatDoesNot()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-scheduler-paths-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         string file = Path.Combine(root, "Qqq.PrintAgent.exe");
         File.WriteAllText(file, "synthetic companion");
         var scheduler = new WindowsTaskScheduler();
         FileAttributes folderAttributes = Assert.NotNull(scheduler.AttributesOf(root));
         FileAttributes fileAttributes = Assert.NotNull(scheduler.AttributesOf(file));
         Assert.True(folderAttributes.HasFlag(FileAttributes.Directory));
         Assert.False(fileAttributes.HasFlag(FileAttributes.Directory));
         Assert.False((folderAttributes | fileAttributes).HasFlag(FileAttributes.ReparsePoint));
         Assert.Null(scheduler.AttributesOf(Path.Combine(root, "missing.exe")));
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>
   /// The name Windows itself gives a well-known account resolves back to that account's identifier, whatever language
   /// Windows spells the name in, and a name no account on this computer has resolves to nothing instead of failing.
   /// </summary>
   [Theory]
   [InlineData(WellKnownSidType.LocalSystemSid, "S-1-5-18")]
   [InlineData(WellKnownSidType.BuiltinAdministratorsSid, "S-1-5-32-544")]
   public void AccountNameResolvesToItsIdentifierAndAnUnknownNameToNothing(WellKnownSidType account, string identifier)
   {
      var scheduler = new WindowsTaskScheduler();
      string name = new SecurityIdentifier(account, null).Translate(typeof(NTAccount)).Value;
      Assert.Equal(identifier, scheduler.ResolveAccountSid(name));
      Assert.Null(scheduler.ResolveAccountSid(Environment.MachineName + "\\qqq-none-" + Guid.NewGuid().ToString("N")[..8]));
   }
}
