/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Windows;
using Qqq.PrintAgent.Windows.Management;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The companion's client for version 2 of the management pipe (ADR-0004). It takes the "send one request, get the
/// parsed reply" function of the pipe and gives each operation the companion sends one method, in the other parts of
/// this class, that takes plain values and returns a <see cref="ServiceResult{T}"/> holding the contract's own result
/// record. No request, reply or payload passes through its methods in either direction, so nothing above it spells an
/// operation's name, builds arguments or reads a reply.
/// <para>
/// It never throws for what a pipe can do, as long as the send function keeps the contract the constructor states
/// for it. A refusal carries the contract's code; a reply that cannot be used says whether an older or a newer
/// service sent it or whether it was simply unreadable; a pipe failure says its cause and, separately, whether the
/// request had left; and a wait the caller ended is a result of its own. Anything else is a programming error and is
/// left to propagate.
/// </para>
/// <para>
/// A result says whether the operation may have been carried out (<see cref="ServiceResult{T}.Unconfirmed"/>), and
/// the client answers "it may have been" whenever it cannot know. A reply that is not in this version of the pipe is
/// one of those cases for every operation the contract's table does not call a read: the shape of a reply names the
/// side to update, and it is not proof that nothing was done.
/// </para>
/// <para>
/// It holds itself to <see cref="MaximumRequestsInFlight"/> requests at once, and sends the two reads that are
/// repeated on a timer and take no arguments one at a time each, with every caller that asks meanwhile sharing the
/// answer.
/// </para>
/// <para>
/// A result carries the service's text as it arrived (a name, a status, a job's title), and the view models that
/// show it give it its printable form; only a refusal's message is already printable, because the contract makes it
/// so when the reply is read.
/// </para>
/// <para>
/// Nothing it writes to the log comes from a request's arguments or from a reply's content. An entry names the
/// operation, the server's identifier, the kind of failure and, for a failure of the pipe itself, the exception's
/// own description of it (its type names and messages), followed at debug level by the exception's full text with
/// its stack. That is safe only because the send function's contract forbids request and reply text in an
/// exception's message. A password never reaches this class; a session credential, an enrollment code and the
/// provider's sign-out request pass through it once, as an argument or as a result, and are not kept.
/// </para>
/// </summary>
public sealed partial class ManagementClient
{
   /// <summary>
   /// How many requests this client has in flight at most. The pipe serves
   /// <see cref="ManagementProtocol.MaximumConcurrentClients"/> clients at once; twelve is one change for each of the
   /// eight servers, the wizard, and the three reads the window repeats, and it leaves four instances for the command
   /// line, the update helper and a second Windows session. A request is in flight until the pipe is done with it,
   /// whether or not its caller still waits.
   /// </summary>
   public const int MaximumRequestsInFlight = 12;



   /// <summary>What a result says when a reply arrived and could not be read. It is fixed, so it can never quote the reply.</summary>
   private const string UnreadableReason = "The service's reply could not be read.";



   /// <summary>What a result says of a read that a service older than this companion answered. A read changes nothing, so there is nothing to check.</summary>
   private const string OlderServiceReason = "The service is older than this companion and does not know its requests. Update the service.";



   /// <summary>What a result says of a request that is not a read and was answered as an older service answers. It names the side to update and does not say that the request was not known: the reply's form does not prove that.</summary>
   private const string OlderServiceUnconfirmedReason = "The service answered as one that is older than this companion does. Update the service. The operation may have been carried out: check before repeating it.";



   /// <summary>What a result says of a read that a service newer than this companion answered. A read changes nothing, so there is nothing to check.</summary>
   private const string NewerServiceReason = "The service is newer than this companion, which cannot read its replies. Update the companion.";



   /// <summary>What a result says of a request that is not a read and was answered in a newer version of the pipe.</summary>
   private const string NewerServiceUnconfirmedReason = NewerServiceReason + " The operation may have been carried out: check before repeating it.";



   /// <summary>What a result says when the client did not send a request because the service would refuse a value in it. It is fixed, so it never repeats the value.</summary>
   private const string RefusedBeforeSendingReason = "The request was not sent: one of its values is not one the service accepts.";



   /// <summary>What an entry calls the target of an operation that names no server.</summary>
   private const string TheComputer = "the computer";



   /// <summary>The entry for a refusal with a code this version does not know.</summary>
   private const string UnknownCodeEntry = "Management request {Operation} on {Target} was refused with a code this version does not know ({Code}); it is handled as {HandledAs}.";



   /// <summary>Sends one request over the pipe and returns the parsed reply. What it must and must not do is the contract the constructor states.</summary>
   private readonly Func<ManagementRequest, Task<ManagementReplyParse>> _send;



   /// <summary>Receives every failure that is turned into a result.</summary>
   private readonly IPresentationLog _log;



   /// <summary>Holds the requests in flight to <see cref="MaximumRequestsInFlight"/>.</summary>
   private readonly RequestCeiling _ceiling = new(MaximumRequestsInFlight);



   /// <summary>The <see cref="ManagementOperations.Snapshot"/> flights: one at the pipe at a time, shared by everyone it can serve.</summary>
   private readonly SingleFlight<ServiceResult<ServiceSnapshot>> _snapshot = new();



   /// <summary>The <see cref="ManagementOperations.TransportGet"/> flights: one at the pipe at a time, shared by everyone who asks meanwhile.</summary>
   private readonly SingleFlight<ServiceResult<TransportResult>> _transport = new();



   /// <summary>The operations that are failing, each with how it last failed, so a failure that repeats is a warning once.</summary>
   private readonly ConcurrentDictionary<(string Operation, string? Server), (ServiceFailure Failure, bool Unconfirmed)> _failing = new();



   /// <summary>The operations whose last answer was a refusal with a code this version does not know, each with that code as an entry shows it, so the same code repeating is a warning once.</summary>
   private readonly ConcurrentDictionary<(string Operation, string? Server), string> _unknownCodes = new();



   /// <summary>
   /// Wraps the pipe's send function. The log receives every failure that is turned into a result. The client is
   /// given no server's name or identifier: each method that acts on a server takes its identifier.
   /// <para>
   /// The send function's contract. It writes the request (<see cref="ManagementJson.Write(ManagementRequest)"/>),
   /// verifies the service, waits the operation's time limit (<see cref="ManagementOperationInfo.ClientTimeLimit"/>)
   /// and returns the reply as <see cref="ManagementJson.ParseReply"/> read it, whatever the reply was: text that
   /// cannot be read is a result of that function and never an exception. What the function throws, or fails its
   /// task with, says what is known about the request:
   /// </para>
   /// <list type="bullet">
   /// <item>Certainly not sent: <see cref="AgentPipeNotSentException"/>, and nothing else. It wraps the cause, and it is thrown only while no byte of the request has left this process (the connect limit, a pipe that could not be opened, a service that was not verified, a request over the size limit).</item>
   /// <item>Outcome unknown: <see cref="IOException"/> (which includes <see cref="InvalidDataException"/>, a reply over the size limit), <see cref="OperationCanceledException"/> and <see cref="TimeoutException"/> (a time limit), <see cref="ObjectDisposedException"/> (a pipe closed under the call), <see cref="UnauthorizedAccessException"/> and <see cref="Win32Exception"/>, each thrown as itself. The client reports every one of them as a request that may have been carried out, whatever its type: a type does not prove when a failure happened.</item>
   /// <item>Nothing else, for anything a pipe can do. An exception of any other type is taken for a programming error and leaves the client as an exception, also when it is wrapped as "not sent".</item>
   /// <item>No exception's message, and no inner exception's, may contain text of the request or of the reply. The client writes an exception's description to the log, at debug level with its stack, and puts its messages in the result an operator is shown; a request can hold a session credential or an enrollment code, and a reply the provider's sign-out request.</item>
   /// </list>
   /// </summary>
   public ManagementClient(Func<ManagementRequest, Task<ManagementReplyParse>> send, IPresentationLog log)
   {
      _send = send ?? throw new ArgumentNullException(nameof(send));
      _log = log ?? throw new ArgumentNullException(nameof(log));
   }



   /// <summary>
   /// Sends one request and turns whatever happens into a result. The request is built first: values the contract
   /// refuses are answered here as the service would answer them, and nothing is sent (<see cref="TryBuild"/>). A
   /// token that is already cancelled means the caller is gone before anything happened, so nothing is sent either.
   /// Then the request takes its place under the ceiling (<see cref="TakePlaceAsync"/>) and is sent
   /// (<see cref="ExchangeAsync"/>). A read that is <paramref name="repeated"/> on a timer is skipped when the ceiling
   /// is reached; every other request waits its turn. A result that <paramref name="fits"/> refuses is a reply for
   /// another subject than the one that was asked about, and is unreadable.
   /// </summary>
   private async Task<ServiceResult<TResult>> AskAsync<TResult>(string operation, Func<string, ManagementRequest> build, CancellationToken cancellationToken,
      Func<TResult, bool>? fits = null, bool repeated = false)
      where TResult : class
   {
      if(!TryBuild(operation, build, out ManagementRequest? request))
      {
         return ServiceResult<TResult>.Refusal(ManagementErrorCode.InvalidRequest, RefusedBeforeSendingReason);
      }
      if(cancellationToken.IsCancellationRequested)
      {
         return NotStarted<TResult>(operation, request.Server);
      }
      if(await TakePlaceAsync<TResult>(request, repeated, cancellationToken).ConfigureAwait(false) is { } notSent)
      {
         return notSent;
      }
      return await ExchangeAsync(request, fits, cancellationToken).ConfigureAwait(false);
   }



   /// <summary>
   /// Sends a request whose place under the ceiling is taken, and reads what comes back. Cancelling the token ends
   /// the wait, not the request: it is the caller's decision and not a failure, so it is a result of its own and is
   /// not counted against the service. A pipe failure is a result too; an exception the send function's contract does
   /// not allow is left to propagate.
   /// </summary>
   private async Task<ServiceResult<TResult>> ExchangeAsync<TResult>(ManagementRequest request, Func<TResult, bool>? fits, CancellationToken cancellationToken)
      where TResult : class
   {
      Task<ManagementReplyParse> pending = SendAsync(request);
      try
      {
         return Read(request, await pending.WaitAsync(cancellationToken).ConfigureAwait(false), fits);
      }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
         return Abandoned<TResult>(request.Op, request.Server, pending);
      }
      catch(Exception exception) when(Classify(exception) is { Failure: not ServiceFailure.None } kind)
      {
         return Failed<TResult>(request, kind, exception);
      }
   }



   /// <summary>
   /// Sends a read that takes no arguments single-flight: one such request is at the pipe at a time, and callers
   /// share it. The flight belongs to no caller, so one caller ending its wait leaves the others their answer.
   /// <para>
   /// By default a caller joins the flight that is in the air and gets its result, and only the first costs a
   /// request; with none in the air and the ceiling reached the read is skipped, as every read repeated on a timer
   /// is. The answer can therefore be that of a request sent before the call was made.
   /// </para>
   /// <para>
   /// A caller that needs an answer <paramref name="sentAfterTheCall"/> is never given one that was sent earlier. It
   /// notes how many flights have started and waits out the last of them, which is over at once unless it is still
   /// in the air. Then it shares a flight that started after it asked, if there is one, and otherwise starts one;
   /// at the ceiling it waits its turn and is not skipped. While it waits, for the flight in the air or for its
   /// turn, nothing has been sent for it, and a token cancelled then ends the call with nothing sent.
   /// </para>
   /// <para>
   /// A flight is started only by a caller that holds a place under the ceiling, and the flight keeps that place
   /// until the pipe is done with it. So every flight was handed to the pipe, and a flight counted later was handed
   /// over later. A caller that took a place and then found a flight to share gives the place back.
   /// </para>
   /// </summary>
   private async Task<ServiceResult<TResult>> ShareAsync<TResult>(SingleFlight<ServiceResult<TResult>> flight, string operation, bool sentAfterTheCall, CancellationToken cancellationToken)
      where TResult : class
   {
      if(cancellationToken.IsCancellationRequested)
      {
         return NotStarted<TResult>(operation, server: null);
      }
      long? startedAfter = null;
      if(sentAfterTheCall)
      {
         startedAfter = flight.Started(out Task? last);
         await (last ?? Task.CompletedTask).WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
         if(cancellationToken.IsCancellationRequested)
         {
            return NotStarted<TResult>(operation, server: null);
         }
      }
      var request = ManagementRequest.For(operation);
      if(!flight.TryShare(startedAfter, out Task<ServiceResult<TResult>>? shared))
      {
         if(await TakePlaceAsync<TResult>(request, repeated: !sentAfterTheCall, cancellationToken).ConfigureAwait(false) is { } notSent)
         {
            return notSent;
         }
         shared = flight.ShareOrStart(startedAfter, () => ExchangeAsync<TResult>(request, fits: null, CancellationToken.None), out bool started);
         if(!started)
         {
            _ceiling.Leave();
         }
      }
      try
      {
         return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
      }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
         return Abandoned<TResult>(operation, server: null, shared);
      }
   }



   /// <summary>
   /// Builds the request, or says that it cannot be built, by the contract's own rules and no others. The contract's
   /// records and its request refuse some values when they are built (a name that breaks the name rule, text that is
   /// no server identifier or no origin); the arguments are then read back the way the service reads them
   /// (<see cref="WithArguments"/>), which refuses a value that is missing; and the request is put to the check the
   /// service routes by (<see cref="ManagementOperations.Check"/>), which refuses an operation on one server that
   /// names none. The service would answer each of these <see cref="ManagementErrorCode.InvalidRequest"/>. The
   /// refusals are fixed text that never repeats what was given, and the entry names the operation and nothing that
   /// was given.
   /// </summary>
   private bool TryBuild(string operation, Func<string, ManagementRequest> build, [NotNullWhen(true)] out ManagementRequest? request)
   {
      try
      {
         request = build(operation);
      }
      catch(ArgumentException)
      {
         request = null;
      }
      if(request is not null && ManagementOperations.Check(request, out _) is null)
      {
         return true;
      }
      _log.Warning("Management request {Operation} was not sent: a value given for it is one the contract refuses.", operation);
      request = null;
      return false;
   }



   /// <summary>
   /// Builds a request with arguments, and reads the arguments back the way the service will
   /// (<see cref="ManagementJson.TryReadArguments"/>). Arguments the contract's own reader does not take are refused
   /// here, as an argument that is wrong, so they are never sent: a value that is missing where the record needs one
   /// (a credential, a code, a profile, a list), and a list with nothing where an item belongs. What that reader
   /// accepts is sent, whatever else the service may think of it; this client adds no rule of its own.
   /// </summary>
   private static ManagementRequest WithArguments<TArguments>(string operation, string? serverId, TArguments arguments)
      where TArguments : class
   {
      var request = ManagementRequest.For(operation, serverId, arguments);
      return ManagementJson.TryReadArguments<TArguments>(request, out _) ? request : throw new ArgumentException("The arguments are not ones the contract reads.", nameof(arguments));
   }



   /// <summary>
   /// The address a request carries for a server's origin: the origin as the contract's one address rule
   /// (<see cref="ServerAddress.TryRead"/>) judges it, written as scheme, host and port with no path. An address that
   /// rule refuses, and none at all, is refused here as an argument that is wrong, with fixed text.
   /// </summary>
   private static string AddressOf(Uri? origin)
      => origin is { IsAbsoluteUri: true } && ServerAddress.TryRead(origin.AbsoluteUri, out Uri? judged, out _) ? judged.GetLeftPart(UriPartial.Authority)
         : throw new ArgumentException("The address is not one a server may have.", nameof(origin));



   /// <summary>True when the address a reply names is the origin that was asked about, each judged by the contract's one address rule, so one place written two ways is one place.</summary>
   private static bool IsAddress(string named, Uri asked)
      => ServerAddress.TryRead(named, out Uri? origin, out _) && string.Equals(AddressOf(origin), AddressOf(asked), StringComparison.Ordinal);



   /// <summary>
   /// Takes the request's place under the ceiling and returns null, or returns the result of a request that is not
   /// sent. A repeated read never waits: with no place free it is skipped. Any other request takes a place that is
   /// free at once (its caller has looked at its token just before), and otherwise waits its turn. When its caller's
   /// token is cancelled while it waits, it is not sent, so a change nobody waits for any more is never made later.
   /// That holds at the last moment too: a turn that comes as the token is cancelled is given back, and the request
   /// is not sent.
   /// </summary>
   private async Task<ServiceResult<TResult>?> TakePlaceAsync<TResult>(ManagementRequest request, bool repeated, CancellationToken cancellationToken)
   {
      if(repeated)
      {
         if(_ceiling.TryEnter())
         {
            return null;
         }
         _log.Debug("Management request {Operation} on {Target} was skipped: {Limit} requests are already in flight. It is asked again on its next tick.", request.Op, Target(request.Server), MaximumRequestsInFlight);
         return ServiceResult<TResult>.Skipped();
      }
      Task turn = _ceiling.EnterAsync(cancellationToken);
      if(turn.IsCompletedSuccessfully)
      {
         return null;
      }
      _log.Debug("Management request {Operation} on {Target} waits its turn: {Limit} requests are already in flight.", request.Op, Target(request.Server), MaximumRequestsInFlight);
      try
      {
         await turn.ConfigureAwait(false);
      }
      catch(OperationCanceledException)
      {
         return NotStarted<TResult>(request.Op, request.Server);
      }
      if(cancellationToken.IsCancellationRequested)
      {
         _ceiling.Leave();
         return NotStarted<TResult>(request.Op, request.Server);
      }
      return null;
   }



   /// <summary>Hands the request to the pipe and gives its place back when the pipe is done with it, however that ends and whether or not the caller still waits. A send function that throws before it returns a task is a task that failed.</summary>
   private async Task<ManagementReplyParse> SendAsync(ManagementRequest request)
   {
      try
      {
         return await _send(request).ConfigureAwait(false);
      }
      finally
      {
         _ceiling.Leave();
      }
   }



   /// <summary>
   /// Reads what came back. An envelope that could not be read is not an answer (<see cref="Unusable"/>). A failure is
   /// a refusal with its code. A success must hold the operation's result record, and one that does not, or that is
   /// not the result that was asked for, is unreadable: the service acted, and what it did is not known here.
   /// </summary>
   private ServiceResult<TResult> Read<TResult>(ManagementRequest request, ManagementReplyParse parse, Func<TResult, bool>? fits)
      where TResult : class
   {
      if(!parse.Succeeded())
      {
         return Unusable<TResult>(request, parse.Problem);
      }
      if(parse.Reply.Error is { } error)
      {
         return Refused<TResult>(request, error);
      }
      if(ManagementJson.TryReadResult(parse.Reply, out TResult? result) && (fits is null || fits(result)))
      {
         Answered(request);
         return ServiceResult<TResult>.Success(result);
      }
      Note(request, (ServiceFailure.UnreadableReply, Unconfirmed: true), "Management request {Operation} on {Target} succeeded, but its result could not be read as {Result}.", request.Op, Target(request.Server), typeof(TResult).Name);
      return ServiceResult<TResult>.Unreachable(ServiceFailure.UnreadableReply, sent: true, UnreadableReason);
   }



   /// <summary>
   /// The result for a reply whose envelope could not be read. Text that is not JSON is what a version 1 service
   /// answers, and a reply in another version is what a newer service answers; the result names the side to update.
   /// Neither is proof that nothing was carried out. That a version 1 service does nothing with a version 2 request
   /// was run against this product's version 1 runtime, but text that is not JSON is also what any failure outside
   /// the envelope looks like, and a reply in another version need not be a refusal of the version. So only a read,
   /// which changes nothing whoever answered, is reported as having nothing to check
   /// (<see cref="IsRead"/>); every other operation is unconfirmed, and its message says so. JSON without a version
   /// has no known sender (a version 1 service never answers in JSON), so it is an answer that arrived and could not
   /// be read, as every other problem is: the service may have acted. The entry names the problem and never the
   /// reply, which this client is never given as text.
   /// </summary>
   private ServiceResult<TResult> Unusable<TResult>(ManagementRequest request, ManagementEnvelopeProblem problem)
   {
      bool nothingToCheck = IsRead(request);
      (ServiceFailure failure, bool unconfirmed, string reason) = problem switch
      {
         ManagementEnvelopeProblem.NotJson => (ServiceFailure.OlderService, !nothingToCheck, nothingToCheck ? OlderServiceReason : OlderServiceUnconfirmedReason),
         ManagementEnvelopeProblem.VersionUnsupported => (ServiceFailure.NewerService, !nothingToCheck, nothingToCheck ? NewerServiceReason : NewerServiceUnconfirmedReason),
         _ => (ServiceFailure.UnreadableReply, true, UnreadableReason),
      };
      Note(request, (failure, unconfirmed), "Management request {Operation} on {Target} got a reply that could not be used ({Failure}: {Problem}).", request.Op, Target(request.Server), failure, problem);
      return ServiceResult<TResult>.Unreachable(failure, unconfirmed, reason);
   }



   /// <summary>True for an operation the contract's table calls a read, which changes nothing whoever answers it. An operation the table does not have is not one; none is ever sent (<see cref="TryBuild"/>).</summary>
   private static bool IsRead(ManagementRequest request) => ManagementOperations.TryFind(request.Op, out ManagementOperationInfo? operation) && operation.Kind == ManagementOperationKind.Read;



   /// <summary>
   /// The result for a refusal. A code this version does not know is handled as
   /// <see cref="ManagementErrorCode.InternalError"/>, as the contract says: the result carries that code and the
   /// service's own message, and no details, because their type is not known either. The log says which code
   /// arrived. The first such refusal of an operation on a target is a warning; the same code repeating is a debug
   /// line instead, as a repeated failure is, so a read that is refused with one on every tick does not flood the
   /// log. Another unknown code, or the same one after the operation was answered in any other way, is a warning
   /// again.
   /// </summary>
   private ServiceResult<TResult> Refused<TResult>(ManagementRequest request, ManagementError error)
   {
      if(ManagementErrorCode.All.Contains(error.Code, StringComparer.Ordinal))
      {
         Answered(request);
         return ServiceResult<TResult>.Refusal(error.Code, error.Message, error);
      }
      string shown = Shown(error.Code);
      bool repeated = _unknownCodes.TryGetValue((request.Op, request.Server), out string? previous) && string.Equals(previous, shown, StringComparison.Ordinal);
      Answered(request);
      _unknownCodes[(request.Op, request.Server)] = shown;
      object?[] values = [request.Op, Target(request.Server), shown, ManagementErrorCode.InternalError];
      if(repeated)
      {
         _log.Debug(UnknownCodeEntry, values);
      }
      else
      {
         _log.Warning(UnknownCodeEntry, values);
      }
      return ServiceResult<TResult>.Refusal(ManagementErrorCode.InternalError, error.Message);
   }



   /// <summary>
   /// Builds the unavailable result for a pipe failure and logs what was swallowed, followed by the full exception,
   /// with its stack, as debug detail. What is described is the pipe's own failure; that no request's arguments and
   /// no reply's text are part of it is the send function's contract (the constructor states it).
   /// </summary>
   private ServiceResult<TResult> Failed<TResult>(ManagementRequest request, (ServiceFailure Failure, bool Sent) kind, Exception exception)
   {
      Note(request, kind, "Management request {Operation} on {Target} did not complete ({Failure}, {Sent}): {Reason}", request.Op, Target(request.Server), kind.Failure,
         kind.Sent ? "request sent" : "nothing sent", QqqSessionDiagnostics.Describe(exception));
      _log.Debug("Management request {Operation} on {Target} failure detail: {Exception}", request.Op, Target(request.Server), exception.ToString());
      return ServiceResult<TResult>.Unreachable(kind.Failure, kind.Sent, QqqSessionDiagnostics.Explain(exception));
   }



   /// <summary>
   /// Writes the entry for a request that brought no usable answer. The first failure of an operation on a target is a
   /// warning; the same failure repeating (the reads run every few seconds while the service is down) is a debug line
   /// instead, so the log is not flooded unless debug logging was asked for. A failure is the same when its cause is
   /// and when it says the same about whether the request had left.
   /// </summary>
   private void Note(ManagementRequest request, (ServiceFailure Failure, bool Unconfirmed) kind, string template, params object?[] values)
   {
      if(_failing.TryGetValue((request.Op, request.Server), out (ServiceFailure Failure, bool Unconfirmed) previous) && previous == kind)
      {
         _log.Debug(template, values);
         return;
      }
      _failing[(request.Op, request.Server)] = kind;
      _log.Warning(template, values);
   }



   /// <summary>Notes that the service answered this operation, with a result or a refusal, and says so once in the log when it had been failing. Whatever unknown code it was last refused with is forgotten; a refusal with one records it again afterwards.</summary>
   private void Answered(ManagementRequest request)
   {
      _unknownCodes.TryRemove((request.Op, request.Server), out _);
      if(_failing.TryRemove((request.Op, request.Server), out _))
      {
         _log.Info("Management request {Operation} on {Target} is answering again.", request.Op, Target(request.Server));
      }
   }



   /// <summary>The result for a caller that had ended its wait before anything was sent.</summary>
   private ServiceResult<TResult> NotStarted<TResult>(string operation, string? server)
   {
      _log.Debug("Management request {Operation} on {Target} was not sent: its wait had ended before it was.", operation, Target(server));
      return ServiceResult<TResult>.NotStarted();
   }



   /// <summary>
   /// The result for a caller that ended its wait after the request was sent. The request is followed to its end, so a
   /// failure arriving after the caller stopped waiting is observed and noted here instead of surfacing later as an
   /// unobserved task exception.
   /// </summary>
   private ServiceResult<TResult> Abandoned<TResult>(string operation, string? server, Task pending)
   {
      _ = pending.ContinueWith(task => _log.Debug("Management request {Operation} on {Target} failed after its wait ended: {Reason}", operation, Target(server),
            QqqSessionDiagnostics.Describe(task.Exception!.GetBaseException())),
         CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      _log.Debug("Management request {Operation} on {Target} is no longer waited for; the service may still carry it out.", operation, Target(server));
      return ServiceResult<TResult>.Abandoned();
   }



   /// <summary>
   /// Names the cause of a pipe failure and whether the request had been sent, or <see cref="ServiceFailure.None"/> for
   /// an exception this client must not swallow. Only a failure the pipe client wrapped as "not sent" says nothing was
   /// sent, and it is classified by what it wraps. Every other failure, whatever its type, is treated as possibly sent:
   /// the type of an exception does not prove when it happened, and "check before repeating" is the safe answer to
   /// doubt.
   /// </summary>
   private static (ServiceFailure Failure, bool Sent) Classify(Exception exception)
      => exception is AgentPipeNotSentException { InnerException: { } cause } ? (Cause(cause), false) : (Cause(exception), true);



   /// <summary>
   /// Maps a pipe failure to its cause. A time limit is one whether it arrives as a cancellation or as a
   /// <see cref="TimeoutException"/>, and a pipe that was closed under the call
   /// (<see cref="ObjectDisposedException"/>) is a disconnection. Any type the send function's contract does not
   /// name is <see cref="ServiceFailure.None"/>, which leaves the exception to propagate.
   /// </summary>
   private static ServiceFailure Cause(Exception exception) => exception switch
   {
      OperationCanceledException or TimeoutException => ServiceFailure.TimedOut,
      UnauthorizedAccessException => ServiceFailure.AccessDenied,
      InvalidDataException => ServiceFailure.InvalidData,
      Win32Exception => ServiceFailure.Native,
      IOException or ObjectDisposedException => ServiceFailure.Disconnected,
      _ => ServiceFailure.None,
   };



   /// <summary>What an entry calls the target of an operation: the server's identifier, or the computer.</summary>
   private static string Target(string? server) => server ?? TheComputer;



   /// <summary>A code as an entry shows it: itself when it is shaped like one of the contract's codes (lower-case letters, digits and underscores, at most 64 of them), and otherwise a fixed label, so text of any other kind that arrived where a code belongs is not written to the log.</summary>
   private static string Shown(string code)
      => code.Length is > 0 and <= 64 && code.All(static letter => char.IsAsciiLetterLower(letter) || char.IsAsciiDigit(letter) || letter == '_') ? code : "not shown";
}
