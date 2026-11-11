/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>What the wizard does when the service refuses one of its requests. <see cref="WizardReactions"/> names one of these for every code of the contract and every operation the wizard sends.</summary>
internal enum WizardReaction
{
   /// <summary>The service's message is shown and the log is pointed at; everything typed is kept. It is also what a code with no entry in the table gets.</summary>
   ShowMessage,

   /// <summary>The refusal is the result of a check: its details are the stages, and the certificate when one was refused.</summary>
   ShowCheck,

   /// <summary>The service did not accept a value the operator typed; the operator corrects it.</summary>
   ValueNotAccepted,

   /// <summary>The service runs no connections because its stored data is not ready; the wizard ends on the Computer page, which says why.</summary>
   StoredDataNotReady,

   /// <summary>An update is being installed; everything typed is kept and the operator tries again afterwards.</summary>
   UpdateInProgress,

   /// <summary>Another program is making a change through the service; everything typed is kept and the operator tries again.</summary>
   Busy,

   /// <summary>A code the contract does not have this operation answer. The service's message is shown as for <see cref="ShowMessage"/>, and the log says that the code was not expected.</summary>
   NotThisOperations,
}
