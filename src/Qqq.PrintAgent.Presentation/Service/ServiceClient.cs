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
/// writes what it swallowed to the log and tells the caller the kind. The wire format is unchanged. The named operations
/// are in the other part of this class.
/// </summary>
public sealed partial class ServiceClient
{
   private const string ErrorMarker = "Error:";
   private readonly Func<PipeRequest, Task<string>> _send;
   private readonly IPresentationLog _log;
   private readonly string _serverName;
   private readonly ConcurrentDictionary<string, ServiceFailure> _failing = new(StringComparer.Ordinal);



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
   /// is left to propagate. Cancelling the token ends the wait, not the request: the service may still carry it out.
   /// </summary>
   private async Task<ServiceResult<T>> ExchangeAsync<T>(PipeRequest request, Func<string, ServiceResult<T>> interpret, CancellationToken cancellationToken)
   {
      ArgumentNullException.ThrowIfNull(request);
      Task<string>? pending = null;
      try
      {
         pending = _send(request);
         string reply = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
         ServiceResult<T> result = interpret(reply);
         Answered(request.Operation);
         return result;
      }
      catch(JsonException exception)
      {
         return Failed<T>(request.Operation, ServiceFailure.UnreadableReply, exception);
      }
      catch(Exception exception) when(Classify(exception, cancellationToken) is var failure && failure != ServiceFailure.None)
      {
         Abandon(pending);
         return Failed<T>(request.Operation, failure, exception);
      }
   }



   /// <summary>Names the kind of a pipe failure, or <see cref="ServiceFailure.None"/> for an exception this client must not swallow. A cancellation counts as the caller's only when the caller's token asked for it; otherwise it was the pipe's time limit.</summary>
   private static ServiceFailure Classify(Exception exception, CancellationToken cancellationToken) => exception switch
   {
      OperationCanceledException => cancellationToken.IsCancellationRequested ? ServiceFailure.Canceled : ServiceFailure.TimedOut,
      UnauthorizedAccessException => ServiceFailure.AccessDenied,
      InvalidDataException => ServiceFailure.InvalidData,
      IOException => ServiceFailure.Disconnected,
      Win32Exception => ServiceFailure.Native,
      _ => ServiceFailure.None,
   };



   /// <summary>Follows a request that was given up on to its end, so a failure arriving after the caller stopped waiting is observed here instead of surfacing later as an unobserved task exception.</summary>
   private static void Abandon(Task<string>? pending)
   {
      if(pending is { IsCompleted: false })
      {
         _ = pending.ContinueWith(static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      }
   }



   /// <summary>
   /// Builds the unavailable result and logs what was swallowed. The first failure of an operation is a warning; the same
   /// failure repeating (the status polls run every few seconds while the service is down) is debug detail, so the log is
   /// not flooded. An unreadable reply is described by its type only, because its text could quote part of the reply.
   /// </summary>
   private ServiceResult<T> Failed<T>(string operation, ServiceFailure failure, Exception exception)
   {
      bool unreadable = failure == ServiceFailure.UnreadableReply;
      string entry = $"Service request '{operation}' did not complete ({failure}): {(unreadable ? exception.GetType().Name : QqqSessionDiagnostics.Describe(exception))}";
      if(_failing.TryGetValue(operation, out ServiceFailure previous) && previous == failure)
      {
         _log.Debug(entry);
      }
      else
      {
         _failing[operation] = failure;
         _log.Warning(entry);
      }
      return ServiceResult<T>.Unreachable(failure, unreadable ? "The service's reply could not be read." : QqqSessionDiagnostics.Explain(exception));
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
      => reply.StartsWith(ErrorMarker, StringComparison.OrdinalIgnoreCase)
         ? ServiceResult<T>.Rejection(reply[ErrorMarker.Length..].Trim(), reply)
         : ServiceResult<T>.Success(read(reply), reply);



   /// <summary>Interprets a reply whose success is a known opening phrase (enrollment and sign-in answer in sentences). Any other reply is a rejection whose message is the whole reply, because that is what the operator has always been shown.</summary>
   private static ServiceResult<string> Opening(string reply, string phrase)
      => reply.StartsWith(phrase, StringComparison.Ordinal) ? ServiceResult<string>.Success(reply, reply) : ServiceResult<string>.Rejection(reply, reply);



   /// <summary>Reads one JSON document with the options the service writes with; an empty document is unreadable rather than a null payload.</summary>
   private static T ReadJson<T>(string reply) where T : class
      => JsonSerializer.Deserialize<T>(reply, PrintApiClient.JsonOptions) ?? throw new JsonException("The reply held no document.");
}
