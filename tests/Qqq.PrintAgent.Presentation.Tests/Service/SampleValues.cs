/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>
/// The plain values a test gives the client's methods, one of each kind. Two sets exist: the values of the contract's
/// golden requests, so what the client sends can be compared with those files, and a set in which every piece of text
/// a caller supplies carries a mark, so a test can search what the client wrote to its log for anything it was given.
/// </summary>
internal sealed record SampleValues(string ServerId, string SecondServerId, string Address, string Name, string NewName, string ProviderOrigin, QqqSessionCredential Credential,
   string Repair, string CallbackAddress, string EnrollmentCode, DeviceProfile ComputerProfile, string PrinterId, PrinterProfile PrinterProfile, string Format, string TestProfile,
   JobQuery Query, string JobId)
{
   /// <summary>What every text value of <see cref="Marked"/> contains, in upper or lower case.</summary>
   internal const string Mark = "CANARY";



   /// <summary>The values of the golden requests under <c>Fixtures/management</c>.</summary>
   internal static SampleValues Golden { get; } = new("3f2b8c1e-5d4a-4f6b-9a7c-0e1d2c3b4a5f", "3f2b8c1e-0000-4f6b-9a7c-0e1d2c3b4a60", "https://qqq.example.test", "Warehouse", "Warehouse east",
      "https://sign-in.example.test", new QqqSessionCredential(new Uri("https://qqq.example.test"), "CANARY-SESSION-COOKIE-7f3a"), SessionSignInArgs.AssociatePairing,
      "io.qrun.qqq-print-agent:/oauth/callback", "CANARY-ENROLLMENT-CODE-55d0", new DeviceProfile("Packing station 7", "Label printing for outbound orders", "West warehouse"), "printer-a1",
      new PrinterProfile("Shipping labels", "Thermal printer", "Dock 2"), "PDF", "alignment",
      new JobQuery("inv", "FAILED", "printer-a1", NeedsAttentionOnly: true, Offset: 50, PageSize: 25, SortBy: "updatedAt", Descending: false), "job-204");



   /// <summary>
   /// Values in which everything a caller supplies as text carries <see cref="Mark"/>: the address, the names, the
   /// provider's origin, the session token, the repair, the callback address, the enrollment code, the profiles, the
   /// printer, the format, the query and the job. The servers' identifiers do not: the client may name a server in
   /// its log.
   /// </summary>
   internal static SampleValues Marked { get; } = new(Golden.ServerId, Golden.SecondServerId, "https://canary-server.example.test", "CANARY name", "CANARY new name",
      "https://canary-sign-in.example.test", new QqqSessionCredential(new Uri("https://canary-server.example.test"), "CANARY-SESSION-TOKEN"), "CANARY-repair",
      "canary-app:/callback", "CANARY-ENROLLMENT-CODE", new DeviceProfile("CANARY computer", "CANARY description", "CANARY location"), "CANARY-printer",
      new PrinterProfile("CANARY display name", "CANARY printer description", "CANARY printer location"), "CANARY-FORMAT", "CANARY-test-profile",
      new JobQuery("CANARY-search", "CANARY-STATUS", "CANARY-printer", NeedsAttentionOnly: true, Offset: 50, PageSize: 25, SortBy: "CANARY-sort", Descending: false), "CANARY-job");
}
