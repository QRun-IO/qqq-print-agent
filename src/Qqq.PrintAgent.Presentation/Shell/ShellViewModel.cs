/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using Qqq.PrintAgent.Presentation.Service;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Shell;

/// <summary>
/// What the window is, without a window: the tree, which page is on screen and its heading, the footer, the tray,
/// what is known of the local service, the page's message, and which servers are busy. It holds the last snapshot
/// and reads a new one, and the transport meter, each time the window's timers tick (the other part of this class).
/// <para>
/// It decides which page is shown. Before the service has answered that is the Computer page, which says what is
/// known. When the service first answers with its stored data ready, the window lands where 1.x opened: on the
/// first server's Printers page, or, when the computer has no server, on the connection wizard. While the stored
/// data is not ready, or the service does not answer, the Computer page stays and says why; the wizard is shown
/// only when the snapshot answered and the stored data is ready. Once the operator has chosen a page themselves the
/// window does not land anywhere by itself. When the server whose page is on screen goes, its neighbour is shown
/// and a message says that the server went. That is said only when a snapshot whose stored data is ready no longer
/// lists the server: when the servers are not listed because the stored data is not ready, the Computer page is
/// shown with the reason, and nothing says that a server was removed, because none was.
/// </para>
/// <para>
/// It does not know the wizard. It asks for one through <see cref="IWizardLauncher"/>, after it has decided that
/// one may start and has shown the wizard's page, and it is told when the wizard has ended
/// (<see cref="WizardEnded"/>). A start that is refused is refused out loud, as a message on the page on screen.
/// </para>
/// <para>
/// It is used from one thread, the window's, which is the thread it was built on, and tells its bindings about
/// every change on that thread: it awaits the client without leaving the thread it was called on. The one thing
/// that can reach it from another thread is the gate, when a command's hold is released there. The tree is not
/// touched then; the log is told once, and the next read shows on every node what the gate says. It never sees a
/// credential.
/// </para>
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
   /// <summary>The companion's client for the management pipe.</summary>
   private readonly ManagementClient _client;

   /// <summary>The gate every command enters; its changes show on the tree's nodes.</summary>
   private readonly OperationGate _gate;

   /// <summary>How a wizard is asked for.</summary>
   private readonly IWizardLauncher _wizard;

   /// <summary>Receives what the shell decided.</summary>
   private readonly IPresentationLog _log;

   /// <summary>Words the times the Computer page shows.</summary>
   private readonly TimestampFormat _time;

   /// <summary>The product's name, for the tray's tooltip.</summary>
   private readonly string _productName;

   /// <summary>Backs <see cref="Snapshot"/>.</summary>
   private ServiceSnapshot? _snapshot;

   /// <summary>False when the service has stopped answering since <see cref="_snapshot"/> was read.</summary>
   private bool _current;

   /// <summary>Backs <see cref="Availability"/>.</summary>
   private ServiceAvailability _availability = ServiceAvailability.Checking;

   /// <summary>Backs <see cref="Footer"/>.</summary>
   private FooterStatus _footer;

   /// <summary>Backs <see cref="Tray"/>.</summary>
   private TrayStatus _tray;

   /// <summary>Backs <see cref="CurrentPage"/>.</summary>
   private PageHeading _currentPage;

   /// <summary>Backs <see cref="WizardOpen"/>.</summary>
   private bool _wizardOpen;

   /// <summary>True once the window has landed on the page a first answer calls for.</summary>
   private bool _landed;

   /// <summary>True once the operator has chosen a page themselves, after which the window lands nowhere by itself.</summary>
   private bool _operatorChose;

   /// <summary>A server's page the wizard asked to be shown before a snapshot listed the server; it is shown when one does.</summary>
   private PageKey? _awaited;

   /// <summary>True once the shell was disposed; nothing is read or shown after that.</summary>
   private bool _disposed;

   /// <summary>The thread the shell was built on, which is the window's. The tree is changed on this thread only.</summary>
   private readonly int _thread = Environment.CurrentManagedThreadId;

   /// <summary>1 once the log was told that the gate changed on another thread than the window's; 0 until then.</summary>
   private int _otherThreadNoted;

   /// <summary>The servers the tree had when a snapshot whose stored data is ready was last shown: a server of these that such a snapshot no longer lists has gone.</summary>
   private IReadOnlyList<ServerNode> _listedWhenReady = [];



   /// <summary>
   /// Builds the shell over the client, the one gate, the way to ask for a wizard, the log, the format of the times
   /// it words and the product's name. Nothing is read until the first tick.
   /// </summary>
   public ShellViewModel(ManagementClient client, OperationGate gate, IWizardLauncher wizard, IPresentationLog log, TimestampFormat time, string productName)
   {
      _client = client ?? throw new ArgumentNullException(nameof(client));
      _gate = gate ?? throw new ArgumentNullException(nameof(gate));
      _wizard = wizard ?? throw new ArgumentNullException(nameof(wizard));
      _log = log ?? throw new ArgumentNullException(nameof(log));
      _time = time ?? throw new ArgumentNullException(nameof(time));
      _productName = productName ?? throw new ArgumentNullException(nameof(productName));
      Busy = new ServerBusy(gate);
      Messages = new PageMessages(Tree.Selected);
      _footer = FooterStatus.From(_availability, []);
      _tray = TrayStatus.From(_availability, _footer, [], _productName);
      _currentPage = ShellMessages.Heading(Tree.Selected, null);
      _gate.Changed += OnGateChanged;
   }



   /// <summary>
   /// Raised after a snapshot was read and everything here follows it, so a page can take what it shows from
   /// <see cref="Snapshot"/>. It says which servers' entries were held back because the server was changed from this
   /// window after the snapshot was asked for; a page leaves what it shows of such a server as it is.
   /// </summary>
   public event EventHandler<SnapshotAppliedEventArgs>? SnapshotApplied;



   /// <summary>Raised after a read of the transport meter ended in any way that changes what <see cref="TransportOf"/> gives.</summary>
   public event EventHandler? TransportChanged;



   /// <summary>The tree.</summary>
   public ServerTree Tree { get; } = new();



   /// <summary>Which servers are busy, and which were changed from this window and when.</summary>
   public ServerBusy Busy { get; }



   /// <summary>The message bar, by page.</summary>
   public PageMessages Messages { get; }



   /// <summary>What the footer says, as something a screen reader is told once: it is announced after <see cref="Footer"/> holds the new sentence, and only when the sentence changed since the shell last announced one. A listener reads the sentence from the event (<see cref="AnnouncementEventArgs.Text"/>).</summary>
   public Announcement FooterAnnouncement { get; } = new();



   /// <summary>The page on screen.</summary>
   public PageKey Selected => Tree.Selected;



   /// <summary>The heading of the page on screen.</summary>
   public PageHeading CurrentPage
   {
      get => _currentPage;
      private set => SetProperty(ref _currentPage, value);
   }



   /// <summary>
   /// The snapshot the window shows: the last one read, except that a server changed from this window since that
   /// snapshot was asked for keeps the entry the window had, which is the one from before the change (the same
   /// object as before), until a snapshot asked for after the change arrives. <see cref="SnapshotApplied"/> names
   /// those servers. Null until one was read.
   /// </summary>
   public ServiceSnapshot? Snapshot
   {
      get => _snapshot;
      private set => SetProperty(ref _snapshot, value);
   }



   /// <summary>What is known of the local service.</summary>
   public ServiceAvailability Availability
   {
      get => _availability;
      private set => SetProperty(ref _availability, value);
   }



   /// <summary>The footer.</summary>
   public FooterStatus Footer
   {
      get => _footer;
      private set => SetProperty(ref _footer, value);
   }



   /// <summary>The tray icon's texts.</summary>
   public TrayStatus Tray
   {
      get => _tray;
      private set => SetProperty(ref _tray, value);
   }



   /// <summary>True from the moment a wizard was asked for until <see cref="WizardEnded"/> says it has ended, whether or not its page is on screen.</summary>
   public bool WizardOpen
   {
      get => _wizardOpen;
      private set => SetProperty(ref _wizardOpen, value);
   }



   /// <summary>
   /// What Windows says about the service itself (stopped, not installed), for the guidance of a service that is not
   /// reached. The Computer page sets it when it has asked Windows; it is used from the next read on.
   /// </summary>
   public string? ServiceControlGuidance { get; set; }



   /// <summary>
   /// The operator chose a page. A page of a server that is not in the tree is not shown, and the answer is false.
   /// Choosing the wizard's page while no wizard is open is asking to add a server (<see cref="StartAddServer"/>).
   /// While a wizard is open it only shows the wizard's page again, so the operator gets back to the one in
   /// progress, whichever kind it is, and the launcher is not asked for anything. Choosing the page that is already
   /// on screen changes nothing: its message stays and no wizard is asked for a second time, so a view may pass
   /// every selection of its tree on, including the one that follows the shell's own move.
   /// </summary>
   public bool Select(PageKey page)
   {
      ArgumentNullException.ThrowIfNull(page);
      if(page == PageKey.Wizard && !WizardOpen)
      {
         return StartAddServer();
      }
      if(page == Selected)
      {
         _operatorChose = true;
         return true;
      }
      if(!Show(page))
      {
         _log.Debug("Page {Page} was asked for and is not in the tree.", page);
         return false;
      }
      _operatorChose = true;
      _awaited = null;
      return true;
   }



   /// <summary>Opens what the footer points at: the first server that needs attention, the wizard when there is no server, or the Computer page. It does nothing while the footer points at nothing.</summary>
   public bool SelectAttention() => Footer.Attention is { } page && Select(page);



   /// <summary>
   /// The operator asked for the wizard that adds a server, in so many words: a button that says so, not the tree's
   /// node (<see cref="Select"/>). When one can be added now, the wizard's page is shown and the wizard is asked
   /// for, also while another wizard is open: its owner decides what becomes of that one. When none can be added
   /// and a wizard is open, its page is shown, so the operator gets back to the one in progress, and nothing new is
   /// asked for. Otherwise the request is refused out loud with the reason: the service has not answered or its
   /// stored data is not ready, or the computer has as many servers as it may hold.
   /// </summary>
   public bool StartAddServer()
   {
      if(ReasonNotToAdd() is not { } reason)
      {
         OpenWizard();
         _wizard.AddServer();
         return true;
      }
      if(WizardOpen)
      {
         _operatorChose = true;
         return Show(PageKey.Wizard);
      }
      return Refuse(reason, "add server");
   }



   /// <summary>The operator asked to sign in again to a server. Refused out loud when the server is not in the tree or is being removed, or when the service has not answered or its stored data is not ready. How many servers the computer has does not matter: the limit is on adding one, and a computer at the limit must still be able to sign in again.</summary>
   public bool StartRepair(string serverId)
   {
      if(ReasonNotToChange(serverId) is { } reason)
      {
         return Refuse(reason, "repair");
      }
      OpenWizard();
      _wizard.Repair(serverId);
      return true;
   }



   /// <summary>The operator asked to pair this computer with a server. Refused out loud as <see cref="StartRepair"/> is, and like it not held to the limit on adding a server.</summary>
   public bool StartPairing(string serverId)
   {
      if(ReasonNotToChange(serverId) is { } reason)
      {
         return Refuse(reason, "pairing");
      }
      OpenWizard();
      _wizard.Pair(serverId);
      return true;
   }



   /// <summary>
   /// The wizard has ended, finished or cancelled, and is gone. <paramref name="open"/> is the page to show now, such
   /// as the server it added. A server the tree does not list yet (the wizard has just added it, and no snapshot
   /// has been read since) is shown as soon as a snapshot lists it, and the Computer page stands in until then.
   /// When no page is given and the wizard's page is on screen, the window goes where it would land: the first
   /// server's Printers page, or the Computer page when the stored data is not ready. With no server and the stored
   /// data ready that is the wizard again, because a computer with no server shows the wizard; the new one is not
   /// asked for here, inside the call of a wizard that is still ending, but when the next snapshot has been read
   /// (and at once if the operator chooses Add server first). Until then the wizard's page stays, with no wizard
   /// open. Nothing in here calls the launcher.
   /// </summary>
   public void WizardEnded(PageKey? open = null)
   {
      SetWizardOpen(false);
      if(open is null || open == PageKey.Wizard)
      {
         if(Selected == PageKey.Wizard && Landing() is var landing && landing != PageKey.Wizard)
         {
            Show(landing);
         }
      }
      else if(Show(open))
      {
         _operatorChose = true;
      }
      else
      {
         _awaited = open;
         if(Selected == PageKey.Wizard)
         {
            Show(PageKey.Computer);
         }
      }
   }



   /// <summary>
   /// Starts a command under a gate's key: a server's identifier, or <see cref="OperationGate.Computer"/>. When the
   /// key is free the command holds it until <paramref name="hold"/> is disposed, and <paramref name="whileBusy"/>
   /// is the sentence a second command is refused with meanwhile. When the key is held the command must not start:
   /// the holder's sentence is shown as a message on the page on screen, and the refusal is logged. Nothing is
   /// dropped without a word and nothing is queued. A command on another key is not affected.
   /// </summary>
   public bool TryBeginCommand(string key, string whileBusy, [NotNullWhen(true)] out IDisposable? hold)
   {
      if(Busy.TryBegin(key, whileBusy, out hold, out string? refusal))
      {
         return true;
      }
      Messages.Show(Selected, ShellMessages.StillWorking(refusal));
      _log.Info("A command on {Key} was refused: another command is still running there.", key);
      return false;
   }



   /// <summary>Stops everything: no read is started or shown after this, and the gate is no longer followed.</summary>
   public void Dispose()
   {
      _disposed = true;
      _gate.Changed -= OnGateChanged;
      Busy.Dispose();
   }



   /// <summary>Shows a page: selects its node, moves the message bar to it (which drops the message that was on screen and keeps those of the page left that were not) and words the heading. False when the page is not in the tree.</summary>
   private bool Show(PageKey page)
   {
      if(!Tree.Select(page))
      {
         return false;
      }
      Messages.MoveTo(page);
      CurrentPage = ShellMessages.Heading(page, Tree.Server(page.ServerId)?.Name);
      OnPropertyChanged(nameof(Selected));
      return true;
   }



   /// <summary>
   /// Goes to a page the shell chose itself. Going to the wizard's page asks for the wizard that adds a server,
   /// unless one is open. A wizard is recorded as open before its page is selected, so a view that passes the
   /// selection on finds one open and asks for nothing, and the launcher is called last.
   /// </summary>
   private void GoTo(PageKey page)
   {
      bool ask = page == PageKey.Wizard && !WizardOpen;
      if(ask)
      {
         SetWizardOpen(true);
      }
      Show(page);
      if(ask)
      {
         _wizard.AddServer();
      }
   }



   /// <summary>Shows the wizard's page for a wizard the operator asked for, recorded as open before its page is selected, as in <see cref="GoTo"/>.</summary>
   private void OpenWizard()
   {
      _operatorChose = true;
      SetWizardOpen(true);
      Show(PageKey.Wizard);
   }



   /// <summary>Records whether a wizard is open, and the Add server node follows: it is enabled and says "in progress" while one is.</summary>
   private void SetWizardOpen(bool open)
   {
      WizardOpen = open;
      Tree.AddServer.Set(ReasonNotToAdd(), open);
   }



   /// <summary>Where the window lands: the first server's Printers page, where 1.x opened; with no server, the wizard when the stored data is ready and the Computer page when it is not.</summary>
   private PageKey Landing()
      => Tree.Servers.Count > 0 ? PageKey.ForServer(PageKind.Printers, Tree.Servers[0].ServerId)
         : Availability.StoredDataReady ? PageKey.Wizard
         : PageKey.Computer;



   /// <summary>Why a server cannot be added now, or null when one can.</summary>
   private string? ReasonNotToAdd()
      => !Availability.StoredDataReady ? Availability.Summary
         : Tree.Servers.Count >= ManagementProtocol.MaximumServers ? ShellMessages.ServerLimit
         : null;



   /// <summary>Why a wizard cannot start on a server now, or null when one can.</summary>
   private string? ReasonNotToChange(string serverId)
      => !Availability.StoredDataReady ? Availability.Summary
         : Tree.Server(serverId) is not { } server ? ShellMessages.ServerGone
         : server.IsRemoving ? ShellMessages.ServerBeingRemoved
         : null;



   /// <summary>Refuses a wizard out loud: the reason is a message on the page on screen, and the log says what was asked for.</summary>
   private bool Refuse(string reason, string asked)
   {
      Messages.Show(Selected, ShellMessages.CannotStart(reason));
      _log.Info("The wizard was asked for ({Asked}) and did not start, because it cannot start now.", asked);
      return false;
   }



   /// <summary>
   /// A command started or ended under a key: the node it belongs to shows it. The gate raises this on whatever
   /// thread entered or released the key. On the window's thread the node follows at once. On any other thread the
   /// tree is left alone, because its nodes tell their bindings on the thread that changes them and a reconcile may
   /// be running on the window's; the log is told once, without the holder's sentence, and the next read shows on
   /// every node what the gate says then.
   /// </summary>
   private void OnGateChanged(object? sender, OperationGateChangedEventArgs e)
   {
      if(Environment.CurrentManagedThreadId == _thread)
      {
         Tree.SetBusy(e.Key, e.Busy);
      }
      else if(Interlocked.Exchange(ref _otherThreadNoted, 1) == 0)
      {
         _log.Info("A command's hold on {Key} changed on another thread than the window's. The tree shows that after the next read. This is said once.", e.Key);
      }
   }
}
