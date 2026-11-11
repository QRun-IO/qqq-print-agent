/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Presentation.SignIn;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Wizard;

/// <summary>
/// Every sentence the connection wizard shows or announces, in one place, so the wording can be read and tested as a
/// whole. Each says what happened, in plain words, and what to do next. None claims more than the wizard knows: a
/// check that was not answered is not said to have failed, and a server is said to have passed only what the
/// service's check tested. None names a control of the old connection dialog, and none can hold a password, a code or
/// a credential, because the wizard is never given one to put in a sentence. Text that came from the service is
/// passed in already in the contract's printable form.
/// </summary>
public static class WizardMessages
{
   /// <summary>The label of the button that asks the service to check the server.</summary>
   public const string Check = "Check";

   /// <summary>The label of that button once a check has failed.</summary>
   public const string CheckAgain = "Check again";

   /// <summary>The label of the button that goes on to the next step.</summary>
   public const string Next = "Next";

   /// <summary>The label of the button that goes back one step.</summary>
   public const string Back = "Back";

   /// <summary>The label of the button that ends the wizard.</summary>
   public const string Cancel = "Cancel";

   /// <summary>The label of the button that opens the server an address already is.</summary>
   public const string OpenServer = "Open server";

   /// <summary>The label of the button that asks for the certificate exception.</summary>
   public const string UseException = "Use the development-only exception";

   /// <summary>The label of the button that drops an accepted certificate exception.</summary>
   public const string DropException = "Check without the exception";

   /// <summary>The title of the certificate warning.</summary>
   public const string ExceptionTitle = "Accept an untrusted certificate?";

   /// <summary>The label of the warning's button that accepts.</summary>
   public const string ExceptionAccept = "Accept for this server";

   /// <summary>What the Server step asks for, shown above its two boxes.</summary>
   public const string ServerIntro = "Enter the address of the QQQ server, for example " + Example + ", and a name to show for it. Check asks the print service on this computer to test the server. It stores nothing and can take up to two minutes.";

   /// <summary>Said once when a check starts.</summary>
   public const string Checking = "Checking the server. This can take up to two minutes.";

   /// <summary>What a second check is refused with while one runs.</summary>
   public const string CheckStillRunning = "The server is still being checked, so this was not started. Wait for the result, or choose Cancel to close the wizard.";

   /// <summary>The address is already a server, and the wizard can open it.</summary>
   public const string AlreadyServer = "This address is already a server on this computer, so there is nothing to add. Choose Open server to go to it, or enter another address.";

   /// <summary>The address is already a server, and the service did not say which in a form the wizard can open.</summary>
   public const string AlreadyServerNotNamed = "This address is already a server on this computer, so there is nothing to add. Find it in the list of servers, or enter another address.";

   /// <summary>Every stage passed, and no sign-in method on offer is one this version knows.</summary>
   public const string NoKnownSignInMethod = "The server passed the print service's checks, but it offers no sign-in method this version of the companion knows, so you could not sign in. Nothing was stored. Update QQQ Print Agent, or ask the server's administrator which sign-in methods it offers.";

   /// <summary>The service answered, and the answer contradicts itself or names a sign-in provider that is no address.</summary>
   public const string CheckUnusable = "The print service answered the check, but its answer could not be used. Nothing was stored. Choose Check again. If this repeats, look under Logs.";

   /// <summary>The request never left this computer.</summary>
   public const string NotReached = "The request did not reach the print service on this computer, so nothing was checked. What you entered is kept. Make sure the QQQ Print Agent service is running, then choose Check. If this repeats, look under Logs for the reason.";

   /// <summary>The request left and no usable answer came back.</summary>
   public const string Unanswered = "No answer that could be used came back from the print service, so the result of the check is not known. A check stores nothing, so it is safe to repeat. Choose Check.";

   /// <summary>A service that speaks the earlier version of the pipe answered.</summary>
   public const string OlderService = "The print service on this computer is an older version than this companion and does not know this request, so nothing was checked. Finish updating QQQ Print Agent so that both are the same version, then choose Check.";

   /// <summary>A service that speaks a later version of the pipe answered.</summary>
   public const string NewerService = "The print service on this computer is a newer version than this companion, so nothing was checked. Update the companion to the same version, then start it again.";

   /// <summary>The service is prepared for an update.</summary>
   public const string UpdateInProgress = "An update is being installed, so the print service is not checking servers right now. Nothing was checked and what you entered is kept. When the update has finished, choose Check.";

   /// <summary>The service's gate is held by another client.</summary>
   public const string Busy = "Another program is making a change through the print service right now. Nothing was checked and what you entered is kept. Wait a moment, then choose Check.";

   /// <summary>The service runs no connections; said as the wizard ends on the Computer page.</summary>
   public const string StoredDataNotReady = "The print service is not running connections because this computer's stored data is not ready, so nothing was checked. The Computer page says why and what to do.";

   /// <summary>The operator dropped an exception that had been accepted.</summary>
   public const string ExceptionDropped = "The certificate exception was dropped. Choose Check to check the server without it.";

   /// <summary>The service did not accept the address as it was sent.</summary>
   public const string AddressRefusedByService = "The print service did not accept this address. Enter the server's address as https:// and its host name, for example " + Example + ", then choose Check.";

   /// <summary>A certificate came with no reason for its refusal.</summary>
   public const string RefusalNotGiven = "Windows refused the certificate, and the print service did not say why.";

   /// <summary>The wizard cannot start: no snapshot has been answered.</summary>
   public const string StartServiceNotAnswered = "The print service has not answered yet, so a server cannot be added now. Try again when the window shows the service's state.";

   /// <summary>The wizard cannot start: the upgrade from 1.x did not finish.</summary>
   public const string StartUpgradeIncomplete = "The upgrade from the earlier version has not finished, so a server cannot be added yet. The Computer page says where it stopped and what to do.";

   /// <summary>The wizard cannot start: a newer build wrote the stored data.</summary>
   public const string StartNewerBuild = "A newer version of QQQ Print Agent wrote this computer's stored data, so this version cannot add a server. The Computer page says which version to install.";

   /// <summary>The wizard cannot start: the stored data is in a state this version does not know.</summary>
   public const string StartStoredDataNotReady = "This computer's stored data is not ready, so a server cannot be added. The Computer page says more.";

   /// <summary>An address in the form the wizard asks for.</summary>
   private const string Example = "https://qqq.example.com";



   /// <summary>The wizard cannot start: the computer holds as many servers as it may.</summary>
   public static string StartServerLimit { get; } = string.Create(CultureInfo.InvariantCulture,
      $"This computer already has {ManagementProtocol.MaximumServers} servers, which is the most it can have. A server that is being removed counts until it is gone. Remove a server before adding another.");



   /// <summary>A step's name.</summary>
   public static string StepName(WizardStep step) => step switch
   {
      WizardStep.Server => "Server",
      WizardStep.SignIn => "Sign in",
      WizardStep.Pairing => "Pair this computer",
      WizardStep.Printers => "Printers",
      _ => "Done",
   };



   /// <summary>The step indicator: which step of how many, and its name.</summary>
   public static string StepTitle(int number, int count, WizardStep step) => string.Create(CultureInfo.InvariantCulture, $"Step {number} of {count}: {StepName(step)}");



   /// <summary>What one stage of the check tests, by the name the contract gives the stage.</summary>
   public static string StageTitle(string stage) => stage switch
   {
      CompatibilityStageResult.Connection => "Reach the address",
      CompatibilityStageResult.Security => "Trusted secure connection",
      CompatibilityStageResult.Server => "QQQ server",
      CompatibilityStageResult.Printing => "Printing extension",
      CompatibilityStageResult.SignIn => "Sign-in method",
      _ => "Another check",
   };



   /// <summary>How a stage went, in words.</summary>
   public static string StageOutcome(CheckStageOutcome outcome) => outcome switch
   {
      CheckStageOutcome.Passed => "Passed",
      CheckStageOutcome.Failed => "Failed",
      CheckStageOutcome.NotReached => "Not checked, because an earlier check failed",
      _ => "Not known",
   };



   /// <summary>A stage row read as one sentence: what it tests, how it went, and why when it gives reasons.</summary>
   public static string StageName(string title, string outcome, IReadOnlyList<string> problems)
   {
      ArgumentNullException.ThrowIfNull(problems);
      return problems.Count == 0 ? title + ": " + outcome + "." : title + ": " + outcome + ". " + string.Join("; ", problems);
   }



   /// <summary>Why Windows refused a certificate, by the name the contract gives the reason. A reason this version does not know is said to be that.</summary>
   public static string Refusal(string refusal) => refusal switch
   {
      PresentedCertificate.UntrustedAuthority => "Windows does not trust the authority that issued this certificate.",
      PresentedCertificate.NameMismatch => "The certificate was issued for a different name or address than the one entered.",
      PresentedCertificate.NotPresented => "The server presented no certificate.",
      _ => "Windows refused the certificate for a reason this version of the companion does not know.",
   };



   /// <summary>What is wrong with the address as typed, and how to write it.</summary>
   public static string Address(ServerAddressProblem problem) => problem switch
   {
      ServerAddressProblem.Empty => "Enter the server's address, for example " + Example + ".",
      ServerAddressProblem.NotSecure => "The address must start with https://. Plain http:// is accepted only for a server on this computer itself.",
      ServerAddressProblem.HasUserInformation => "The address must not contain a user name or a password. Remove them; you sign in at the next step.",
      ServerAddressProblem.HasQueryOrFragment => "The address must not contain a question mark or a hash sign, or anything after one. Enter only the server's address, for example " + Example + ".",
      ServerAddressProblem.HasPath => "Enter only the server's address, without a path after the host name, for example " + Example + ".",
      ServerAddressProblem.HostNotNameable => "The host name in this address cannot be used. Enter the server's host name, or its IP address without a zone.",
      _ => "This is not a complete address. Enter it with https:// in front, for example " + Example + ".",
   };



   /// <summary>Which part of the name rule a name breaks, and what to do; null for a name that passes.</summary>
   public static string? Name(ServerNameProblem problem) => problem switch
   {
      ServerNameProblem.None => null,
      ServerNameProblem.Empty => "Enter a name for this server.",
      ServerNameProblem.TooLong => string.Create(CultureInfo.InvariantCulture, $"The name is too long. Use at most {ServerName.MaximumCharacters} characters."),
      ServerNameProblem.NotPrintable => "The name contains a character that cannot be shown, such as a line break. Remove it.",
      ServerNameProblem.ShowsNothing => "The name shows nothing on screen. Use letters or digits.",
      ServerNameProblem.SquareBracket => "The name must not contain square brackets. Remove them.",
      _ => "The name looks like a server identifier, which the command line would take for one. Put a word in front, for example Server.",
   };



   /// <summary>Every stage passed and no exception is involved.</summary>
   public static string Passed(SignInOffer methods) => "The server passed all five checks. " + Methods(methods) + " Nothing is stored yet. Choose Next to sign in.";



   /// <summary>
   /// Every stage passed under the certificate exception the operator accepted. The addresses the exception covers are
   /// named: the server's, and the sign-in provider's when the check named one at another address. With
   /// <paramref name="review"/> the operator is seeing the provider's address for the first time and is asked to look
   /// before going on.
   /// </summary>
   public static string PassedUnderException(SignInOffer methods, IReadOnlyList<string> origins, bool review)
   {
      ArgumentNullException.ThrowIfNull(origins);
      string covers = origins.Count > 1
         ? "It accepts a certificate Windows does not trust from two addresses: " + origins[0] + ", the server, and " + origins[1] + ", the sign-in provider the server names."
         : "It accepts a certificate Windows does not trust from " + origins[0] + " only. The check named no sign-in provider at another address.";
      return "The server passed all five checks with the development-only certificate exception." + (review ? " Before you go on, look at what the exception covers. " : " ") + covers + " " + Methods(methods)
         + " Nothing is stored yet: the exception is stored with the server when it is added. Choose Next to go on with the exception, or choose " + DropException + " to drop it.";
   }



   /// <summary>The check did not pass and the service gave its stages. The stage that failed is named when the wizard knows it.</summary>
   public static string Failed(string? failedStage)
      => (failedStage is null ? "The server did not pass the check. The list of checks shows how far it got." : "The server did not pass the check: \"" + failedStage + "\" failed. The reasons are in the list of checks.")
         + " Nothing was stored. Correct the problem, then choose Check again.";



   /// <summary>The check did not pass and the service gave no stages, only its message.</summary>
   public static string FailedWithoutDetails(string message)
      => "The server did not pass the check, and the print service sent no details." + Said(message) + " Nothing was stored. Choose Check again to repeat the check.";



   /// <summary>
   /// Windows refused a certificate. The two ways forward are named: the lasting fix always, and the development-only
   /// exception when it is offered. When the refusal came back although the exception had just been accepted, the
   /// sentence says that the exception does not cover it and was dropped.
   /// </summary>
   public static string CertificateRefused(string? presentedBy, bool exceptionOffered, bool exceptionDidNotCover)
      => (exceptionDidNotCover ? "The certificate exception does not cover what Windows refused, so it was dropped. " : string.Empty)
         + (presentedBy is null ? "Windows refused the certificate that was presented." : "Windows refused the certificate presented by " + presentedBy + ".")
         + " The print service checks certificates under its own account, not yours. The certificate and the reasons are shown. The lasting fix is to install the issuing authority's root certificate in this computer's Trusted Root Certification Authorities store, then choose Check again."
         + (exceptionOffered ? " For a development server only, you can choose " + UseException + " instead."
            : exceptionDidNotCover ? string.Empty
            : " The development-only exception is not offered, because it only covers a certificate from an authority Windows does not trust.");



   /// <summary>The service refused the check with its own message, which is shown, and the log is pointed at.</summary>
   public static string ServiceSaid(string message)
      => "The print service could not carry out the check" + (string.IsNullOrWhiteSpace(message) ? " and gave no reason." : ".") + Said(message) + " Nothing was stored. Look under Logs for more, then choose Check to try again.";



   /// <summary>
   /// The certificate warning's text. It names the server's address. When the sign-in provider's address is known it
   /// names that too; when the check named none at another address it says so; and when it is not known yet, because
   /// the server's own certificate was refused before the sign-in description could be read, it says that the
   /// provider's address will be shown after the check.
   /// </summary>
   public static string ExceptionWarning(string server, string? provider, bool providerKnown)
   {
      const string Head = "This is for a development server only. If you accept, the print service and this companion will accept a certificate that Windows does not trust from ";
      const string Risk = " could then read or change what is sent, including your sign-in and print jobs.";
      const string Tail = " Nothing is stored until the server is added.";
      if(!providerKnown)
      {
         return Head + server + ". Anyone who can get between this computer and that address" + Risk
            + " The server's sign-in provider is not known yet: it can only be read once the server's certificate is accepted. If the server names one at another address, the exception covers that address too, and it will be shown after the check, before you go on."
            + Tail;
      }
      return provider is null
         ? Head + server + ". The check named no sign-in provider at another address. Anyone who can get between this computer and that address" + Risk + Tail
         : Head + "two addresses: " + server + ", the server, and " + provider + ", the sign-in provider the server names. Anyone who can get between this computer and either address" + Risk + Tail;
   }



   /// <summary>The sign-in methods a check found, as a sentence.</summary>
   private static string Methods(SignInOffer methods)
      => methods is { Password: true, Browser: true } ? "It offers sign-in with a user name and password, and sign-in through the browser."
         : methods.Password ? "It offers sign-in with a user name and password."
         : "It offers sign-in through the browser.";



   /// <summary>The service's own message as a sentence of its own, or nothing when the service said nothing.</summary>
   private static string Said(string message)
   {
      string said = message.Trim();
      return said.Length == 0 ? string.Empty : " It said: " + said + (said[^1] is '.' or '!' or '?' ? string.Empty : ".");
   }
}
