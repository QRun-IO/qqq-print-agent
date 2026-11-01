/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.Principal;
using System.Xml.Linq;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The sign-in startup task launches the companion with elevated rights, so what counts as "our" task is checked strictly: it must
/// name the right user and installed executable, run only as that interactive user, and uninstall may remove only exact matches.
/// </summary>
public sealed class AgentStartupTests
{
   /// <summary>Windows may rewrite the task's user as an account name instead of the SID; the task is still recognized as ours when the name resolves to the same SID, in either the principal, the logon trigger, or both.</summary>
   [Theory]
   [InlineData(true, false)]
   [InlineData(false, true)]
   [InlineData(true, true)]
   public void SchedulerNormalizedAccountNamesRetainTheExactTaskIdentity(bool namedPrincipal, bool namedTrigger)
   {
      using var identity = WindowsIdentity.GetCurrent();
      string sid = identity.User!.Value;
      var document = XDocument.Parse(AgentStartup.CreateTaskXml(sid));
      XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
      if(namedPrincipal)
      {
         Assert.Single(document.Descendants(ns + "Principal")).Element(ns + "UserId")!.Value = identity.Name;
      }
      if(namedTrigger)
      {
         Assert.Single(document.Descendants(ns + "LogonTrigger")).Element(ns + "UserId")!.Value = identity.Name;
      }
      Assert.True(AgentStartup.IsOwnedTaskDefinition("\\QQQ Print Agent " + sid, document.ToString()));
   }



   /// <summary>An account name that resolves fine but belongs to a different SID than the task claims is not accepted, so a task cannot be adopted by naming an existing user.</summary>
   [Fact]
   public void ResolvableAccountForADifferentSidCannotAuthorizeTaskOwnership()
   {
      const string OtherSid = "S-1-5-21-100-200-300-1001";
      using var identity = WindowsIdentity.GetCurrent();
      var document = XDocument.Parse(AgentStartup.CreateTaskXml(OtherSid));
      XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
      Assert.Single(document.Descendants(ns + "LogonTrigger")).Element(ns + "UserId")!.Value = identity.Name;
      Assert.False(AgentStartup.IsOwnedTaskDefinition("\\QQQ Print Agent " + OtherSid, document.ToString()));
   }



   /// <summary>The generated task runs only the installed companion with the tray argument, for exactly one user, at that user's logon, as an interactive session with the highest available level and no stored password.</summary>
   [Fact]
   public void SignInTaskUsesOnlyTheInstalledExecutableAndCurrentInteractiveIdentity()
   {
      const string UserSid = "S-1-5-21-100-200-300-1001";
      var document = XDocument.Parse(AgentStartup.CreateTaskXml(UserSid));
      XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
      XElement principal = Assert.Single(document.Descendants(ns + "Principal"));
      Assert.Equal(UserSid, principal.Element(ns + "UserId")?.Value);
      Assert.Equal("InteractiveToken", principal.Element(ns + "LogonType")?.Value);
      Assert.Equal("HighestAvailable", principal.Element(ns + "RunLevel")?.Value);
      Assert.Equal(UserSid, Assert.Single(document.Descendants(ns + "LogonTrigger")).Element(ns + "UserId")?.Value);
      Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "QQQ Print Agent", "App", "Qqq.PrintAgent.exe"), Assert.Single(document.Descendants(ns + "Command")).Value);
      Assert.Equal("--tray", Assert.Single(document.Descendants(ns + "Arguments")).Value);
      Assert.Empty(document.Descendants(ns + "Password"));
   }



   /// <summary>Only the installed companion in its Program Files location may register an elevated startup task: a development build, a same-named file elsewhere, or no executable at all is refused.</summary>
   [Fact]
   public void DevelopmentAndMissingExecutableCannotAuthorizeElevatedStartup()
   {
      Assert.False(AgentStartup.IsInstalledCompanion(Environment.ProcessPath));
      Assert.False(AgentStartup.IsInstalledCompanion(Path.Combine(Path.GetTempPath(), "Qqq.PrintAgent.exe")));
      Assert.False(AgentStartup.IsInstalledCompanion(null));
   }



   /// <summary>Uninstall deletes a scheduled task only when its name and full definition match what the installer creates; a different name, command, executable, logon type, extra action or unparsable XML leaves the task alone.</summary>
   [Fact]
   public void UninstallRecognizesOnlyExactOwnedTaskDefinitions()
   {
      const string UserSid = "S-1-5-21-100-200-300-1001";
      string name = "\\QQQ Print Agent " + UserSid;
      string xml = AgentStartup.CreateTaskXml(UserSid);
      Assert.True(AgentStartup.IsOwnedTaskDefinition(name, xml));
      Assert.False(AgentStartup.IsOwnedTaskDefinition("\\Unrelated application " + UserSid, xml));
      Assert.False(AgentStartup.IsOwnedTaskDefinition(name + "-2", xml));
      Assert.False(AgentStartup.IsOwnedTaskDefinition(name, xml.Replace("--tray", "--different-command", StringComparison.Ordinal)));
      Assert.False(AgentStartup.IsOwnedTaskDefinition(name, xml.Replace("Qqq.PrintAgent.exe", "Other.exe", StringComparison.Ordinal)));
      Assert.False(AgentStartup.IsOwnedTaskDefinition(name, xml.Replace("InteractiveToken", "ServiceAccount", StringComparison.Ordinal)));
      Assert.False(AgentStartup.IsOwnedTaskDefinition(name, "<invalid"));
      var document = XDocument.Parse(xml);
      XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
      XElement actions = document.Root!.Element(ns + "Actions")!;
      actions.Add(new XElement(actions.Elements().Single()));
      Assert.False(AgentStartup.IsOwnedTaskDefinition(name, document.ToString()));
   }
}
