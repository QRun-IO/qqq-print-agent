/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Tests.Service;

/// <summary>
/// Every operation of the management contract, as the companion's client stands to it: sent by one method, or not the
/// companion's to send, with the reason. The tests that must hold for every operation walk these two lists, and one
/// of them requires the lists to cover the contract's table exactly, so an operation added to the contract fails here
/// until the client sends it or this file says why it does not.
/// </summary>
internal static class CompanionCalls
{
   /// <summary>The operations the companion does not send, each with the reason.</summary>
   internal static IReadOnlyDictionary<string, string> NotTheCompanions { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
   {
      [ManagementOperations.ServersList] = "The snapshot carries the servers, in the operator's order.",
      [ManagementOperations.ComputerGetProfile] = "The snapshot carries the computer's profile.",
      [ManagementOperations.UpdatePrepare] = "The MSI update service sends it itself, around the installer, and not through this client.",
      [ManagementOperations.UpdateResume] = "The MSI update service and its helper send it themselves, after the installer, and not through this client.",
   };



   /// <summary>The operations the companion sends, each with the one method that sends it.</summary>
   internal static IReadOnlyList<CompanionCall> All { get; } =
   [
      Call(ManagementOperations.Snapshot, nameof(ManagementClient.SnapshotAsync), (client, _, stop) => client.SnapshotAsync(stop)),
      Call(ManagementOperations.TransportGet, nameof(ManagementClient.TransportAsync), (client, _, stop) => client.TransportAsync(stop)),
      Call(ManagementOperations.ServersCheck, nameof(ManagementClient.CheckServerAsync), (client, values, stop) => client.CheckServerAsync(values.Address, acceptCertificateException: true, stop)),
      Call(ManagementOperations.ServersAdd, nameof(ManagementClient.AddServerAsync),
         (client, values, stop) => client.AddServerAsync(values.Address, values.Name, acceptCertificateException: true, values.Check, values.Credential, stop)),
      Call(ManagementOperations.ServersReorder, nameof(ManagementClient.ReorderServersAsync), (client, values, stop) => client.ReorderServersAsync([values.SecondServerId, values.ServerId], stop)),
      Call(ManagementOperations.ComputerSetProfile, nameof(ManagementClient.SetComputerProfileAsync), (client, values, stop) => client.SetComputerProfileAsync(values.ComputerProfile, stop)),
      Call(ManagementOperations.PrintersDiscover, nameof(ManagementClient.DiscoverPrintersAsync), (client, _, stop) => client.DiscoverPrintersAsync(stop)),
      Call(ManagementOperations.LogsGetSettings, nameof(ManagementClient.GetLogSettingsAsync), (client, _, stop) => client.GetLogSettingsAsync(stop)),
      Call(ManagementOperations.LogsSetSettings, nameof(ManagementClient.SetLogSettingsAsync),
         (client, _, stop) => client.SetLogSettingsAsync(retentionDays: 14, maxFileMegabytes: 50, segmentsPerDay: 5, debugLogging: true, stop)),
      Call(ManagementOperations.LogsClear, nameof(ManagementClient.ClearLogsAsync), (client, _, stop) => client.ClearLogsAsync(stop)),
      Call(ManagementOperations.ServersRename, nameof(ManagementClient.RenameServerAsync), (client, values, stop) => client.RenameServerAsync(values.ServerId, values.NewName, stop)),
      Call(ManagementOperations.ServersRemove, nameof(ManagementClient.RemoveServerAsync), (client, values, stop) => client.RemoveServerAsync(values.ServerId, stop)),
      Call(ManagementOperations.SessionSignIn, nameof(ManagementClient.SignInAsync), (client, values, stop) => client.SignInAsync(values.ServerId, values.Credential, values.Repair, stop)),
      Call(ManagementOperations.SessionSignOut, nameof(ManagementClient.SignOutAsync), (client, values, stop) => client.SignOutAsync(values.ServerId, values.CallbackAddress, stop)),
      Call(ManagementOperations.PairingEnroll, nameof(ManagementClient.EnrollAsync), (client, values, stop) => client.EnrollAsync(values.ServerId, values.EnrollmentCode, stop)),
      Call(ManagementOperations.ConnectionReconnect, nameof(ManagementClient.ReconnectAsync), (client, values, stop) => client.ReconnectAsync(values.ServerId, stop)),
      Call(ManagementOperations.TrustGet, nameof(ManagementClient.GetTrustAsync), (client, values, stop) => client.GetTrustAsync(values.ServerId, stop)),
      Call(ManagementOperations.TrustSet, nameof(ManagementClient.SetTrustAsync),
         (client, values, stop) => client.SetTrustAsync(values.ServerId, acceptCertificateException: true, values.Check, stop)),
      Call(ManagementOperations.PrintersShare, nameof(ManagementClient.SharePrinterAsync), (client, values, stop) => client.SharePrinterAsync(values.ServerId, values.PrinterId, shared: true, stop)),
      Call(ManagementOperations.PrintersSetProfile, nameof(ManagementClient.SetPrinterProfileAsync),
         (client, values, stop) => client.SetPrinterProfileAsync(values.ServerId, values.PrinterId, values.PrinterProfile, stop)),
      Call(ManagementOperations.PrintersTestPrint, nameof(ManagementClient.TestPrintAsync),
         (client, values, stop) => client.TestPrintAsync(values.ServerId, values.PrinterId, values.Format, values.TestProfile, stop)),
      Call(ManagementOperations.JobsQuery, nameof(ManagementClient.QueryJobsAsync), (client, values, stop) => client.QueryJobsAsync(values.ServerId, values.Query, stop)),
      Call(ManagementOperations.JobsDetails, nameof(ManagementClient.JobDetailsAsync), (client, values, stop) => client.JobDetailsAsync(values.ServerId, values.JobId, stop)),
      Call(ManagementOperations.JobsRetry, nameof(ManagementClient.RetryJobAsync), (client, values, stop) => client.RetryJobAsync(values.ServerId, values.JobId, confirmDuplicateRisk: true, stop)),
      Call(ManagementOperations.JobsCancel, nameof(ManagementClient.CancelJobAsync), (client, values, stop) => client.CancelJobAsync(values.ServerId, values.JobId, stop)),
      Call(ManagementOperations.JobsAcknowledge, nameof(ManagementClient.AcknowledgeJobAsync), (client, values, stop) => client.AcknowledgeJobAsync(values.ServerId, values.JobId, stop)),
   ];



   /// <summary>The names of the operations the companion sends, one test case each.</summary>
   public static TheoryData<string> Sent => [.. All.Select(call => call.Operation)];



   /// <summary>The call that sends an operation.</summary>
   internal static CompanionCall For(string operation) => All.Single(call => string.Equals(call.Operation, operation, StringComparison.Ordinal));



   /// <summary>Describes one call, hiding its result type behind <see cref="CallResult"/>.</summary>
   private static CompanionCall Call<TResult>(string operation, string method, Func<ManagementClient, SampleValues, CancellationToken, Task<ServiceResult<TResult>>> send)
      => new(operation, method, async (client, values, stop) => CallResult.Of(await send(client, values, stop)));
}
