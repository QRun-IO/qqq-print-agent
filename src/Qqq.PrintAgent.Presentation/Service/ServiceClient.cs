/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Qqq.PrintAgent.Core.Authentication;
using Qqq.PrintAgent.Core.Printing;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Service;

/// <summary>
/// The companion's one way to talk to the local service. It takes the raw "send a request, get reply text" delegate and
/// returns a <see cref="ServiceResult{T}"/>: the service's "Error:" marker, the reply openings that mean success, and the
/// failures the pipe client raises are all recognised here and nowhere else. It never throws for those failures; it
/// writes what it swallowed to the log and tells the caller the cause and whether the request had been sent. The wire
/// format is unchanged. The named operations are in the other part of this class.
/// </summary>
public sealed partial class ServiceClient
{
   private const string ErrorMarker = "Error:";
   private readonly Func<PipeRequest, Task<string>> _send;
   private readonly IPresentationLog _log;
   private readonly string _serverName;
   private readonly ConcurrentDictionary<string, (ServiceFailure Failure, bool Sent)> _failing = new(StringComparer.Ordinal);



   /// <summary>Wraps the raw send delegate. The server name is what a successful sign-in reply is recognised by, and the log receives every failure that is turned into a result.</summary>
   public ServiceClient(Func<PipeRequest, Task<string>> send, IPresentationLog log, string serverName)
   {
      _send = send ?? throw new ArgumentNullException(nameof(send));
      _log = log ?? throw new ArgumentNullException(nameof(log));
      _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
   }



   /// <summary>Sends a request whose successful reply is plain text for the operator, such as a confirmation sentence.</summary>
   public Task<ServiceResult<string>> SendAsync(PipeRequest request, CancellationToken cancellationToken = default)
      => ExchangeAsync(request, static reply => Marked(reply, static text => text), cancellationToken);



   /// <summary>Sends a request whose successful reply is one JSON document of the given type.</summary>
   public Task<ServiceResult<T>> SendAsync<T>(PipeRequest request, CancellationToken cancellationToken = default) where T : class
      => ExchangeAsync(request, static reply => Marked(reply, ReadJson<T>), cancellationToken);



   /// <summary>
   /// Sends one request and turns whatever happens into a result. A pipe failure of a known kind becomes an unavailable
   /// result, an unreadable reply becomes an unavailable result of its own kind, and anything else (a programming error)
   /// is left to propagate. Cancelling the token ends the wait, not the request: that is the caller's decision rather than
   /// a failure, so it is a result of its own, logged as debug detail and not counted against the service. A token that is
   /// already cancelled means the caller is gone before anything happened, so nothing is sent at all and the result says so.
   /// </summary>
   private async Task<ServiceResult<T>> ExchangeAsync<T>(PipeRequest request, Func<string, ServiceResult<T>> interpret, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      if(cancellationToken.IsCancellationRequested)
      {
         _log.Debug($"Service request '{request.Operation}' was not sent: its wait had ended before it started.");
         return ServiceResult<T>.NotStarted();
      }
      Task<string>? pending = null;
      try
      {
         pending = _send(request);
         string reply = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
         ServiceResult<T> result = interpret(reply);
         Answered(request.Operation);
         return result;
      }
      catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested)
      {
         Abandon(request.Operation, pending);
         _log.Debug($"Service request '{request.Operation}' is no longer waited for; the service may still carry it out.");
         return ServiceResult<T>.Abandoned();
      }
      catch(JsonException exception)
      {
         return Failed<T>(request.Operation, (ServiceFailure.UnreadableReply, Sent: true), exception);
      }
      catch(Exception exception) when(Classify(exception) is { Failure: not ServiceFailure.None } kind)
      {
         return Failed<T>(request.Operation, kind, exception);
      }
   }



   /// <summary>
   /// Names the cause of a pipe failure and whether the request had been sent, or <see cref="ServiceFailure.None"/> for an
   /// exception this client must not swallow. Only a failure the pipe client wrapped as "not sent" says nothing was sent,
   /// and it is classified by what it wraps. Every other failure, whatever its type, is treated as possibly sent: the type
   /// of an exception does not prove when it happened, and "check before repeating" is the safe answer to doubt.
   /// </summary>
   private static (ServiceFailure Failure, bool Sent) Classify(Exception exception)
      => exception is AgentPipeNotSentException { InnerException: { } cause } ? (Cause(cause), false) : (Cause(exception), true);



   /// <summary>Maps a pipe failure to its cause.</summary>
   private static ServiceFailure Cause(Exception exception) => exception switch
   {
      OperationCanceledException => ServiceFailure.TimedOut,
      UnauthorizedAccessException => ServiceFailure.AccessDenied,
      InvalidDataException => ServiceFailure.InvalidData,
      Win32Exception => ServiceFailure.Native,
      IOException => ServiceFailure.Disconnected,
      _ => ServiceFailure.None,
   };



   /// <summary>Follows a request that was given up on to its end, so a failure arriving after the caller stopped waiting is observed and noted here instead of surfacing later as an unobserved task exception.</summary>
   private void Abandon(string operation, Task<string>? pending)
   {
      if(pending is { IsCompleted: false })
      {
         _ = pending.ContinueWith(task => _log.Debug($"Service request '{operation}' failed after its wait ended: {QqqSessionDiagnostics.Describe(task.Exception!.GetBaseException())}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      }
   }



   /// <summary>
   /// Builds the unavailable result and logs what was swallowed. The first failure of an operation is a warning; the same
   /// failure repeating (the status polls run every few seconds while the service is down) is a debug line instead, so the
   /// log is not flooded unless debug logging was asked for. Every failure is followed by its full exception as debug
   /// detail. An unreadable reply is logged as what it is, an answer that arrived and could not be read, and is described
   /// by its type only, because its text could quote part of the reply.
   /// </summary>
   private ServiceResult<T> Failed<T>(string operation, (ServiceFailure Failure, bool Sent) kind, Exception exception)
   {
      bool unreadable = kind.Failure == ServiceFailure.UnreadableReply;
      string entry = unreadable
         ? $"Service request '{operation}' was answered, but the answer could not be read ({exception.GetType().Name})."
         : $"Service request '{operation}' did not complete ({kind.Failure}, {(kind.Sent ? "request sent" : "nothing sent")}): {QqqSessionDiagnostics.Describe(exception)}";
      if(_failing.TryGetValue(operation, out (ServiceFailure Failure, bool Sent) previous) && previous == kind)
      {
         _log.Debug(entry);
      }
      else
      {
         _failing[operation] = kind;
         _log.Warning(entry);
      }
      if(!unreadable)
      {
         _log.Debug($"Service request '{operation}' failure detail: {exception}");
      }
      return ServiceResult<T>.Unreachable(kind.Failure, kind.Sent, unreadable ? "The service's reply could not be read." : QqqSessionDiagnostics.Explain(exception));
   }



   /// <summary>Notes that the service answered this operation, and says so once in the log when it had been failing.</summary>
   private void Answered(string operation)
   {
      if(_failing.TryRemove(operation, out _))
      {
         _log.Info($"Service request '{operation}' is answering again.");
      }
   }



   /// <summary>Interprets a reply that uses the service's "Error:" marker: a marked reply is a rejection carrying the text after the marker, and anything else is read as the payload.</summary>
   private static ServiceResult<T> Marked<T>(string reply, Func<string, T> read)
      => IsMarked(reply) ? ServiceResult<T>.Rejection(Unmarked(reply), reply) : ServiceResult<T>.Success(read(reply), reply);



   /// <summary>
   /// Interprets a reply whose success is a known opening phrase (enrollment and sign-in answer in sentences). Any other reply
   /// is a rejection: its message is the reply without the "Error:" marker when it has one, and the whole reply stays
   /// available for the messages that have always shown it whole.
   /// </summary>
   private static ServiceResult<T> Opening<T>(string reply, string phrase, Func<string, T> read)
      => reply.StartsWith(phrase, StringComparison.Ordinal) ? ServiceResult<T>.Success(read(reply), reply) : ServiceResult<T>.Rejection(Unmarked(reply), reply);



   /// <summary>True when the reply starts with the service's error marker, with or without a following space and in any letter case.</summary>
   private static bool IsMarked(string reply) => reply.StartsWith(ErrorMarker, StringComparison.OrdinalIgnoreCase);



   /// <summary>The reply without its error marker and surrounding spaces, or the reply itself when it has no marker.</summary>
   private static string Unmarked(string reply) => IsMarked(reply) ? reply[ErrorMarker.Length..].Trim() : reply;



   /// <summary>Reads one JSON document with the options the service writes with; an empty document is unreadable rather than a null payload.</summary>
   private static T ReadJson<T>(string reply) where T : class
      => Deserialize<T>(reply) ?? throw new JsonException("The reply held no document.");



   /// <summary>
   /// Turns reply text into a document. Some reply types check their own values when they are built and refuse a missing
   /// one with an argument exception; from here that is one more way a reply can be unreadable, so it becomes the same
   /// exception as malformed JSON instead of escaping this client as a programming error.
   /// </summary>
   private static T? Deserialize<T>(string reply) where T : class
   {
      try
      {
         return JsonSerializer.Deserialize<T>(reply, PrintApiClient.JsonOptions);
      }
      catch(ArgumentException exception)
      {
         throw new JsonException("The reply held a value its document does not accept.", exception);
      }
   }
}
