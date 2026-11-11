/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// What the footer shows for the whole computer: the worst health across the servers, one sentence, how many
/// servers are in each state, and the page its link opens, which is the first server that needs attention.
/// <para>
/// With one server the sentence is that server's own. For a paired server in the states 1.x had, that is what the
/// 1.x footer said; ADR-0005 lists the states in which an operator who upgraded from 1.x reads something else.
/// With several it is the count in each state, worst first. With none it says to add a
/// server and is red, as 1.x is red before a computer is connected. While the service has not answered, or its stored
/// data is not ready, the sentence says that and the link opens the Computer page, which says why. The health of a
/// computer that is still being asked for the first time is unknown, not red and not healthy.
/// </para>
/// <para>
/// While printing is paused for an update, a computer that has servers says that, whatever the servers say, and is
/// never green: nothing is printed until the pause ends, so "Connected and ready" would not be true of the
/// computer. The link opens the Computer page, which says until when. The counts stay the servers' own.
/// </para>
/// </summary>
public sealed record FooterStatus(HealthBadge Health, string Text, int NeedAttention, int Unknown, int NotReady, int Ready, PageKey? Attention, string? AttentionText)
{
   /// <summary>The states in the order the footer lists their counts: worst first.</summary>
   private static readonly HealthLevel[] s_worstFirst = [HealthLevel.Red, HealthLevel.Unknown, HealthLevel.Orange, HealthLevel.Green];



   /// <summary>Builds the footer from what is known of the service and from the servers' states, in the snapshot's order.</summary>
   public static FooterStatus From(ServiceAvailability availability, IReadOnlyList<ServerStatus> servers)
   {
      ArgumentNullException.ThrowIfNull(availability);
      ArgumentNullException.ThrowIfNull(servers);
      if(!availability.StoredDataReady)
      {
         bool checking = availability.Kind == ServiceAvailabilityKind.Checking;
         return Counted(HealthBadge.For(checking ? HealthLevel.Unknown : HealthLevel.Red), OfService(availability.Kind), servers, checking ? null : PageKey.Computer,
            checking ? null : ShellMessages.OpenComputer);
      }
      if(servers.Count == 0)
      {
         return Counted(HealthBadge.For(HealthLevel.Red), ShellMessages.NoServers, servers, PageKey.Wizard, ShellMessages.AddServer);
      }
      HealthLevel worst = HealthStatus.Worst(servers.Select(server => server.Health.Level));
      if(availability.Kind == ServiceAvailabilityKind.UpdatePaused)
      {
         return Counted(HealthBadge.For(HealthStatus.Worst([worst, HealthLevel.Orange])), ShellMessages.FooterUpdatePaused, servers, PageKey.Computer, ShellMessages.OpenComputer);
      }
      ServerStatus? first = worst == HealthLevel.Green ? null : servers.First(server => server.Health.Level == worst);
      string text = servers.Count == 1 ? servers[0].Alone
         : ShellMessages.Servers(servers.Count, s_worstFirst.Select(level => (Level: level, Count: servers.Count(server => server.Health.Level == level)))
            .Where(state => state.Count > 0).Select(state => ShellMessages.Count(state.Level, state.Count)));
      return Counted(HealthBadge.For(worst), text, servers, first is null ? null : PageKey.Server(first.ServerId), first is null ? null : ShellMessages.Open(first.Name));
   }



   /// <summary>The footer with the count of servers in each state filled in.</summary>
   private static FooterStatus Counted(HealthBadge health, string text, IReadOnlyList<ServerStatus> servers, PageKey? attention, string? attentionText)
      => new(health, text, servers.Count(server => server.Health.Level == HealthLevel.Red), servers.Count(server => server.Health.Level == HealthLevel.Unknown),
         servers.Count(server => server.Health.Level == HealthLevel.Orange), servers.Count(server => server.Health.Level == HealthLevel.Green), attention, attentionText);



   /// <summary>The footer's sentence while the service has not answered or its stored data is not ready.</summary>
   private static string OfService(ServiceAvailabilityKind kind) => kind switch
   {
      ServiceAvailabilityKind.Checking => ShellMessages.FooterChecking,
      ServiceAvailabilityKind.UpgradeIncomplete => ShellMessages.FooterUpgradeIncomplete,
      ServiceAvailabilityKind.NewerBuild => ShellMessages.FooterNewerBuild,
      ServiceAvailabilityKind.StoredDataNotReady => ShellMessages.FooterStoredDataNotReady,
      ServiceAvailabilityKind.OlderService => ShellMessages.FooterOlderService,
      ServiceAvailabilityKind.NewerService => ShellMessages.FooterNewerService,
      ServiceAvailabilityKind.Unreadable => ShellMessages.FooterUnreadable,
      ServiceAvailabilityKind.Refused => ShellMessages.FooterRefused,
      _ => ShellMessages.FooterNotReached,
   };
}
