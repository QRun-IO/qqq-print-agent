/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>What the in-app update tells the operator when the service did not answer "Ready for update." to its request to pause printing.</summary>
public sealed class MsiUpdateServiceTests
{
   /// <summary>
   /// "Not ready for update" and "Update preparation was interrupted" are the service's own accounts of why it cannot be
   /// updated now. Each is shown whole, as the service worded it, after one sentence saying that nothing was installed;
   /// nothing is added that the service did not say.
   /// </summary>
   [Theory]
   [InlineData("Not ready for update: 2 job(s) were still in progress after 25 seconds. They will finish; try the update again afterwards.")]
   [InlineData("Update preparation was interrupted: printing was resumed, or the pause ran out, while preparation was waiting. Try the update again.")]
   public void ServiceAccountOfWhyItIsNotReady_IsShownAsTheServiceWordedIt(string reply)
      => Assert.Equal("The update was not started, and the current version remains installed. " + reply, MsiUpdateService.PreparationNotCompleted(reply));



   /// <summary>
   /// Any other answer (an error, or a reply this build does not know) is shown as an answer the update could not use. It
   /// does not tell the operator to let printing finish, because printing may have nothing to do with it.
   /// </summary>
   [Theory]
   [InlineData("Error: Unknown command.")]
   [InlineData("Printing resumed.")]
   [InlineData("")]
   public void AnyOtherReply_IsShownAsAnAnswerTheUpdateCouldNotUse(string reply)
   {
      string shown = MsiUpdateService.PreparationNotCompleted(reply);

      Assert.Equal("The service could not prepare for an update, and the current version remains installed. The service answered: " + reply, shown);
      Assert.DoesNotContain("printing", shown[..^reply.Length], StringComparison.OrdinalIgnoreCase);
   }
}
