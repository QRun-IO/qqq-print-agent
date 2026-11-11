/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>Tests of the harness's own promises, the ones no test of the runtime would notice were broken.</summary>
public sealed class SingleServerHarnessTests
{
   /// <summary>
   /// The harness refuses the installed service's pipe name, in any letter case, because pipe names are compared without
   /// regard to case and a test must never listen where the companion and the command line of this computer connect. The
   /// refusal comes before the runtime is built: the state folder the harness was given is not created.
   /// </summary>
   [Theory]
   [InlineData("QqqPrintAgent")]
   [InlineData("qqqPRINTAGENT")]
   public void InstalledServicePipeName_IsRefused_BeforeAnythingIsBuilt(string name)
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-harness-refusal-" + Guid.NewGuid().ToString("N"));
      ArgumentException refusal = Assert.Throws<ArgumentException>(() => new SingleServerHarness(directory, _ => { }, new RealtimeRuntimeFixture.SyntheticFilePrinter(), pipeName: name));
      Assert.Equal("pipeName", refusal.ParamName);
      Assert.False(Directory.Exists(directory));
   }
}
