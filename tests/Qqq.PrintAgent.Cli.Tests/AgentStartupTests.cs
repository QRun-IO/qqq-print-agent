/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Xml.Linq;
using Qqq.PrintAgent.Windows;


namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentStartupTests
{
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

   [Fact]
   public void DevelopmentAndMissingExecutableCannotAuthorizeElevatedStartup()
   {
      Assert.False(AgentStartup.IsInstalledCompanion(Environment.ProcessPath));
      Assert.False(AgentStartup.IsInstalledCompanion(Path.Combine(Path.GetTempPath(), "Qqq.PrintAgent.exe")));
      Assert.False(AgentStartup.IsInstalledCompanion(null));
   }

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
