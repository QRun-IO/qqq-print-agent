/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Xml.Linq;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Checks the installer's machine-state safety step without installing anything: the embedded PowerShell must be correctly escaped for the MSI formatter, scheduled in the right order, and syntactically valid.</summary>
public sealed class InstallerPreflightTests
{
   /// <summary>
   /// Reads the real installer definition, undoes the MSI escaping of brackets and braces, and confirms the resulting script has the
   /// expected content (type names, common-data folder, try block) and no leftover escapes. The step must run before folders are
   /// created, after InstallExecute at sequence 6500, with the major upgrade scheduled after install. The script is then handed
   /// to PowerShell only to be parsed (never run), proving it is syntactically valid.
   /// </summary>
   [Fact]
   public async Task InstallerEscapesFormattedLiteralsAndPowerShellParsesWithoutExecution()
   {
      var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Package.wxs"));
      XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
      XElement package = document.Root!.Element(ns + "Package")!;
      XElement action = package.Elements(ns + "CustomAction").Single(element => string.Equals((string?)element.Attribute("Id"), "ValidateMachineStateBeforeFolders", StringComparison.Ordinal));
      string command = (string)action.Attribute("ExeCommand")!;
      Assert.Contains("[\\[]Security.Principal.SecurityIdentifier[\\]]", command, StringComparison.Ordinal);
      string formatted = command.Replace("[\\[]", "[", StringComparison.Ordinal).Replace("[\\]]", "]", StringComparison.Ordinal)
         .Replace("[\\{]", "{", StringComparison.Ordinal).Replace("[\\}]", "}", StringComparison.Ordinal);
      string script = formatted[(formatted.IndexOf("-Command \"", StringComparison.Ordinal) + 10)..^1];
      Assert.True(script.Contains("[Security.Principal.SecurityIdentifier]", StringComparison.Ordinal), script);
      Assert.Contains("[Environment]::GetFolderPath('CommonApplicationData')", script, StringComparison.Ordinal);
      Assert.Contains("try {", script, StringComparison.Ordinal);
      Assert.DoesNotContain("[\\", script, StringComparison.Ordinal);
      XElement sequence = package.Element(ns + "InstallExecuteSequence")!;
      Assert.Equal("6500", (string?)sequence.Element(ns + "InstallExecute")?.Attribute("Sequence"));
      Assert.Equal("CreateFolders", (string?)sequence.Elements(ns + "Custom").Single(element => string.Equals((string?)element.Attribute("Action"), "ValidateMachineStateBeforeFolders", StringComparison.Ordinal)).Attribute("Before"));
      Assert.Equal("afterInstallExecute", (string?)package.Element(ns + "MajorUpgrade")!.Attribute("Schedule"));
      ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
      { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true };
      start.ArgumentList.Add("-NoProfile");
      start.ArgumentList.Add("-NonInteractive");
      start.ArgumentList.Add("-Command");
      start.ArgumentList.Add("$null=[scriptblock]::Create([Console]::In.ReadToEnd()); exit 0");
      using Process process = Process.Start(start)!;
      await process.StandardInput.WriteAsync(script);
      process.StandardInput.Close();
      string error = await process.StandardError.ReadToEndAsync();
      using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
      await process.WaitForExitAsync(timeout.Token);
      Assert.True(process.ExitCode == 0, error);
   }

}
