/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The one way version 2 of the management pipe is written and read. Names on the wire are camel case; a member that
/// has no value is left out; dates are ISO 8601 in UTC; the text is one line, so it fits the pipe's newline framing.
/// Requests are read strictly: the envelope is checked member by member, and arguments refuse an unknown member, a
/// missing required one and a null where a value is needed, because a request the service does not fully understand
/// must not be half-obeyed. Replies are read leniently: an unknown member is skipped, so a newer service can add one
/// without breaking an older client.
/// </summary>
public static class ManagementJson
{
   private static readonly JsonDocumentOptions s_documentOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 };



   /// <summary>The options everything is written with, and replies are read with: unknown members are skipped.</summary>
   public static JsonSerializerOptions Options { get; } = CreateOptions(strict: false);



   /// <summary>The options request arguments are read with: an unknown member, a missing required member and a null for a member that needs a value are each refused.</summary>
   public static JsonSerializerOptions StrictOptions { get; } = CreateOptions(strict: true);



   /// <summary>Writes a request as the one line the pipe carries.</summary>
   public static string Write(ManagementRequest request) => JsonSerializer.Serialize(request, Options);



   /// <summary>Writes a reply as the one line the pipe carries.</summary>
   public static string Write(ManagementReply reply) => JsonSerializer.Serialize(reply, Options);



   /// <summary>
   /// Reads a request's envelope and tells apart everything that can be wrong with it. Size is checked before anything is
   /// parsed. The version is checked before the other members, because a request of another version may rightly have
   /// members this one does not know. After that the envelope allows exactly <c>v</c>, <c>op</c>, <c>server</c> and
   /// <c>args</c>, each once. Whether the operation exists, and whether it should name a server, is not decided here
   /// but by <see cref="ManagementOperations.Check"/>, and the arguments are read by <see cref="TryReadArguments"/>.
   /// </summary>
   public static ManagementRequestParse ParseRequest(string? text)
   {
      ManagementEnvelopeProblem problem = ParseObject(text, ManagementProtocol.MaximumRequestCharacters, out JsonDocument? document);
      if(document is null)
      {
         return new ManagementRequestParse(null, problem);
      }
      using(document)
      {
         return ReadRequest(document.RootElement);
      }
   }



   /// <summary>
   /// Reads a reply's envelope. A success must carry a result object and a failure an error with a code and a message;
   /// members this version does not know are skipped. The result and the details are read into their own types by
   /// <see cref="TryReadResult"/> and <see cref="TryReadDetails"/>.
   /// </summary>
   public static ManagementReplyParse ParseReply(string? text)
   {
      ManagementEnvelopeProblem problem = ParseObject(text, ManagementProtocol.MaximumReplyCharacters, out JsonDocument? document);
      if(document is null)
      {
         return new ManagementReplyParse(null, problem);
      }
      using(document)
      {
         return ReadReply(document.RootElement);
      }
   }



   /// <summary>
   /// Reads a request's arguments into the operation's own record, strictly. It returns false when the request has no
   /// arguments, when a member is unknown, missing or of the wrong kind, or when a value is refused by the type it belongs
   /// to (a session credential that is not a valid one, for instance); the caller answers
   /// <see cref="ManagementErrorCode.InvalidRequest"/>. What was wrong is not returned, because it may quote a secret.
   /// </summary>
   public static bool TryReadArguments<TArguments>(ManagementRequest request, [NotNullWhen(true)] out TArguments? arguments)
      where TArguments : class
   {
      ArgumentNullException.ThrowIfNull(request);
      arguments = request.Args is { } element ? Read<TArguments>(element, StrictOptions) : null;
      return arguments is not null;
   }



   /// <summary>Reads a successful reply's result into the operation's own record, skipping members this version does not know. It returns false when the reply has no result or the result does not fit the type.</summary>
   public static bool TryReadResult<TResult>(ManagementReply reply, [NotNullWhen(true)] out TResult? result)
      where TResult : class
   {
      ArgumentNullException.ThrowIfNull(reply);
      result = reply.Result is { } element ? Read<TResult>(element, Options) : null;
      return result is not null;
   }



   /// <summary>Reads an error's details into the type its code names, skipping members this version does not know. It returns false when the error has no details or they do not fit the type.</summary>
   public static bool TryReadDetails<TDetails>(ManagementError error, [NotNullWhen(true)] out TDetails? details)
      where TDetails : class
   {
      ArgumentNullException.ThrowIfNull(error);
      details = error.Details is { } element ? Read<TDetails>(element, Options) : null;
      return details is not null;
   }



   /// <summary>True for text shaped like an operation name: lower-case letters, optionally followed by one dot and a camel-case word, at most 64 characters. A name of another shape cannot be an operation of any version, so it is refused as malformed, not looked up.</summary>
   internal static bool IsOperationName(string name)
   {
      int dot = name.IndexOf('.', StringComparison.Ordinal);
      string area = dot < 0 ? name : name[..dot];
      string action = dot < 0 ? "none" : name[(dot + 1)..];
      return name.Length <= 64 && area.Length > 0 && area.All(char.IsAsciiLetterLower)
         && action.Length > 0 && char.IsAsciiLetterLower(action[0]) && action.All(char.IsAsciiLetter);
   }



   /// <summary>Builds the serializer options. Both sets write the same text; they differ only in how much a reader forgives.</summary>
   private static JsonSerializerOptions CreateOptions(bool strict)
   {
      JsonSerializerOptions options = new()
      {
         PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
         DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
         PropertyNameCaseInsensitive = false,
         NumberHandling = JsonNumberHandling.Strict,
         ReadCommentHandling = JsonCommentHandling.Disallow,
         AllowTrailingCommas = false,
         MaxDepth = 32,
         UnmappedMemberHandling = strict ? JsonUnmappedMemberHandling.Disallow : JsonUnmappedMemberHandling.Skip,
         RespectRequiredConstructorParameters = strict,
         RespectNullableAnnotations = strict,
      };
      options.Converters.Add(new UtcDateTimeConverter());
      options.MakeReadOnly(populateMissingResolver: true);
      return options;
   }



   /// <summary>Checks the size, parses the text and requires an object. On success the document is handed to the caller, who disposes it.</summary>
   private static ManagementEnvelopeProblem ParseObject(string? text, int maximumCharacters, out JsonDocument? document)
   {
      document = null;
      if(text is null)
      {
         return ManagementEnvelopeProblem.NotJson;
      }
      if(text.Length > maximumCharacters)
      {
         return ManagementEnvelopeProblem.TooLarge;
      }
      JsonDocument parsed;
      try
      {
         parsed = JsonDocument.Parse(text, s_documentOptions);
      }
      catch(JsonException)
      {
         return ManagementEnvelopeProblem.NotJson;
      }
      if(parsed.RootElement.ValueKind != JsonValueKind.Object)
      {
         parsed.Dispose();
         return ManagementEnvelopeProblem.NotAnObject;
      }
      document = parsed;
      return ManagementEnvelopeProblem.None;
   }



   /// <summary>Says what is wrong with an envelope's version, or nothing: it must be present and be exactly this contract's version as a whole number.</summary>
   private static ManagementEnvelopeProblem CheckVersion(JsonElement root)
   {
      if(!root.TryGetProperty("v", out JsonElement version))
      {
         return ManagementEnvelopeProblem.VersionMissing;
      }
      return version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int value) && value == ManagementProtocol.Version
         ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.VersionUnsupported;
   }



   /// <summary>Reads the members of a request object whose root is known to be an object.</summary>
   private static ManagementRequestParse ReadRequest(JsonElement root)
   {
      ManagementEnvelopeProblem problem = CheckVersion(root);
      string? operation = null;
      string? server = null;
      JsonElement? arguments = null;
      HashSet<string> seen = new(StringComparer.Ordinal);
      foreach(JsonProperty member in root.EnumerateObject())
      {
         if(problem != ManagementEnvelopeProblem.None)
         {
            break;
         }
         JsonValueKind kind = member.Value.ValueKind;
         problem = (seen.Add(member.Name), member.Name) switch
         {
            (false, _) => ManagementEnvelopeProblem.UnexpectedMember,
            (true, "v") => ManagementEnvelopeProblem.None,
            (true, "op") => kind == JsonValueKind.String ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.OperationMissing,
            (true, "server") => kind == JsonValueKind.String && ManagementProtocol.IsServerId(member.Value.GetString()) ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.ServerMalformed,
            (true, "args") => kind == JsonValueKind.Object ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.ArgumentsNotAnObject,
            _ => ManagementEnvelopeProblem.UnexpectedMember,
         };
         operation = member.NameEquals("op") ? member.Value.ToString() : operation;
         server = member.NameEquals("server") ? member.Value.ToString() : server;
         arguments = member.NameEquals("args") ? member.Value.Clone() : arguments;
      }
      if(problem == ManagementEnvelopeProblem.None && operation is null)
      {
         problem = ManagementEnvelopeProblem.OperationMissing;
      }
      if(problem == ManagementEnvelopeProblem.None && !IsOperationName(operation!))
      {
         problem = ManagementEnvelopeProblem.OperationMalformed;
      }
      return problem == ManagementEnvelopeProblem.None
         ? new ManagementRequestParse(new ManagementRequest(ManagementProtocol.Version, operation!, server, arguments), ManagementEnvelopeProblem.None)
         : new ManagementRequestParse(null, problem);
   }



   /// <summary>Reads the members of a reply object whose root is known to be an object.</summary>
   private static ManagementReplyParse ReadReply(JsonElement root)
   {
      ManagementEnvelopeProblem problem = CheckVersion(root);
      if(problem != ManagementEnvelopeProblem.None)
      {
         return new ManagementReplyParse(null, problem);
      }
      bool hasResult = root.TryGetProperty("result", out JsonElement result) && result.ValueKind == JsonValueKind.Object;
      ManagementError? error = root.TryGetProperty("error", out JsonElement failure) ? ReadError(failure) : null;
      ManagementReply? reply = root.TryGetProperty("ok", out JsonElement ok) switch
      {
         true when ok.ValueKind == JsonValueKind.True && hasResult => new ManagementReply(ManagementProtocol.Version, true, result.Clone()),
         true when ok.ValueKind == JsonValueKind.False && error is not null => new ManagementReply(ManagementProtocol.Version, false, Error: error),
         _ => null,
      };
      return new ManagementReplyParse(reply, reply is null ? ManagementEnvelopeProblem.ReplyIncomplete : ManagementEnvelopeProblem.None);
   }



   /// <summary>Reads an error object, or returns null when it lacks a code or a message as text. Details of any kind but an object are dropped, because every details type is an object.</summary>
   private static ManagementError? ReadError(JsonElement error)
   {
      if(error.ValueKind != JsonValueKind.Object
         || !error.TryGetProperty("code", out JsonElement code) || code.ValueKind != JsonValueKind.String
         || !error.TryGetProperty("message", out JsonElement message) || message.ValueKind != JsonValueKind.String)
      {
         return null;
      }
      JsonElement? details = error.TryGetProperty("details", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? found.Clone() : null;
      return new ManagementError(code.ToString(), message.ToString(), details);
   }



   /// <summary>Reads one element into a type, returning null for anything that does not fit it instead of throwing: a shape the serializer refuses, or a value the type's own constructor refuses.</summary>
   private static TValue? Read<TValue>(JsonElement element, JsonSerializerOptions options)
      where TValue : class
   {
      try
      {
         return element.Deserialize<TValue>(options);
      }
      catch(Exception exception) when(exception is JsonException or ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
      {
         return null;
      }
   }



   /// <summary>
   /// Writes every date and time as ISO 8601 in UTC with a trailing Z, whatever offset the value had, and reads any ISO
   /// 8601 date and time back as UTC, so two computers in different time zones exchange the same text for the same instant.
   /// </summary>
   private sealed class UtcDateTimeConverter : JsonConverter<DateTimeOffset>
   {
      /// <summary>Reads an ISO 8601 date and time; text without an offset is taken as UTC.</summary>
      public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
         => DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value)
            ? value : throw new JsonException("A date and time must be ISO 8601.");



      /// <summary>Writes the instant in UTC with seven fractional digits and a trailing Z.</summary>
      public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
         => writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
   }
}
