/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Fakes;

/// <summary>Synthetic version 2 snapshots for the shell's tests, built from the contract's own records, so each test states only what it is about. By default a server is paired, signed in, authorized, connected and green, with one ready printer shared.</summary>
internal static class Snapshots
{
   /// <summary>The identifier of the one Windows printer the default computer has, which the default server shares.</summary>
   internal const string PrinterId = "printer-a1";



   /// <summary>The identifier of the server with a number: a different one for every number, in the one form the contract writes.</summary>
   internal static string Id(int number) => string.Create(CultureInfo.InvariantCulture, $"3f2b8c1e-5d4a-4f6b-9a7c-{number:x12}");



   /// <summary>A server. Its name is "Server n" unless one is given.</summary>
   internal static ServerSnapshot Server(int number = 1, string? name = null, string health = ServerSnapshot.Green, string status = "Connected. Waiting for print jobs.",
      bool paired = true, bool signedIn = true, bool authorized = true, bool removing = false, bool exception = false, bool stateUnreadable = false, int attention = 0,
      string transport = "CONNECTED", bool paused = false, string connection = "CONNECTED", string[]? shared = null)
      => new(Id(number), name ?? string.Create(CultureInfo.InvariantCulture, $"Server {number}"), string.Create(CultureInfo.InvariantCulture, $"https://server{number}.example.test"), number - 1,
         health, status, attention,
         ServerAuthentication.From(new AgentAuthenticationStatus(signedIn, signedIn ? "principal-4" : null, signedIn ? "tenant-2" : null, authorized ? "Signed in. Printing is enabled." : "Sign in to enable printing.", authorized)),
         paired, paired ? "device-17" : null,
         new AgentTransportSnapshot("WSS", transport, 1024, 2048, 1.5, 2.5, new DateTimeOffset(2026, 11, 10, 12, 4, 5, TimeSpan.Zero)),
         new DateTimeOffset(2026, 11, 10, 15, 4, 1, TimeSpan.Zero),
         new ServerControl("SUPPORTED", new DeviceDesiredState(3, paused, connection)),
         [.. (shared ?? [PrinterId]).Select(printer => new ServerPrinter(printer, PrinterServerState.Approved))],
         new PrinterServerSync(PrinterServerSync.Reported),
         new CertificateExceptionState(exception, string.Create(CultureInfo.InvariantCulture, $"https://server{number}.example.test")),
         stateUnreadable, removing);



   /// <summary>A Windows printer of the computer.</summary>
   internal static ComputerPrinter Printer(string id = PrinterId, string status = "READY") => new(id, "ZDesigner ZT411", "ZDesigner", "USB001", status, ["PDF"]);



   /// <summary>The computer's part of a snapshot: by default stored data that is ready, no update pause and one ready printer.</summary>
   internal static ComputerSnapshot Computer(StoredState? state = null, UpdateState? update = null, ComputerPrinter[]? printers = null)
      => new(printers ?? [Printer()], new DateTimeOffset(2026, 11, 10, 15, 3, 25, TimeSpan.Zero), new ServiceState("2.0.0", new DateTimeOffset(2026, 11, 10, 12, 4, 5, TimeSpan.Zero)),
         update ?? new UpdateState(false), new LogSettings(), state ?? new StoredState(StoredState.Ready), new DeviceProfile("Packing station 7"));



   /// <summary>A snapshot of a computer whose stored data is ready, with these servers.</summary>
   internal static ServiceSnapshot Of(params ServerSnapshot[] servers) => new(Computer(), servers);



   /// <summary>A snapshot with as many default servers as asked for, numbered from one.</summary>
   internal static ServiceSnapshot WithServers(int count) => Of([.. Enumerable.Range(1, count).Select(number => Server(number))]);
}
