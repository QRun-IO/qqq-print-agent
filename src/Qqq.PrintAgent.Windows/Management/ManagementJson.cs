/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Qqq.PrintAgent.Windows.Management;

/// <summary>
/// The one way version 2 of the management pipe is written and read. Names on the wire are camel case; a member that
/// has no value is left out; dates are ISO 8601 in UTC; the text is one line, so it fits the pipe's newline framing.
/// Nothing is read from an object that has the same member twice, at any depth, because readers disagree about which
/// value counts. A required member that is missing, a null where a value is needed and a null inside a list the
/// record knows make the whole record unreadable, in a request and in a reply alike: a record with a hole in it is
/// not half-used. Requests are also read strictly about what they may contain: the envelope is checked member by
/// member and arguments refuse a member they do not know, because a request the service does not fully understand
/// must not be half-obeyed. Replies skip a member they do not know, whatever it holds, so a newer service can add one
/// without breaking an older client; for the same reason a client treats a vocabulary value it does not know (a
/// health, a stage, an outcome) as unknown, never as a failure to read the reply. An envelope is read only by
/// <see cref="ParseRequest"/> and <see cref="ParseReply"/>: handed to the serializer directly, the envelope types
/// refuse to be read, so the checks those two make cannot be walked around.
/// </summary>
public static class ManagementJson
{
   /// <summary>How a document is parsed: no comments, no trailing commas, a bounded depth, and no object with a repeated member.</summary>
   private static readonly JsonDocumentOptions s_documentOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32, AllowDuplicateProperties = false };



   /// <summary>The same, but tolerating a repeated member. It is used only to find out whether a repeated member is the reason the strict parse refused a text, so that case can be told apart from text that is not JSON at all.</summary>
   private static readonly JsonDocumentOptions s_repeatTolerantOptions = s_documentOptions with { AllowDuplicateProperties = true };



   /// <summary>The empty object that stands for arguments a request left out.</summary>
   private static readonly JsonElement s_noArguments = JsonSerializer.SerializeToElement(new NoResult());



   /// <summary>The options everything is written with, and replies are read with: a member the reader does not know is skipped.</summary>
   public static JsonSerializerOptions Options { get; } = CreateOptions(skipUnknownMembers: true);



   /// <summary>The options request arguments are read with: a member the reader does not know is refused.</summary>
   public static JsonSerializerOptions StrictOptions { get; } = CreateOptions(skipUnknownMembers: false);



   /// <summary>Writes a request as the one line the pipe carries.</summary>
   public static string Write(ManagementRequest request) => JsonSerializer.Serialize(request, Options);



   /// <summary>Writes a reply as the one line the pipe carries.</summary>
   public static string Write(ManagementReply reply) => JsonSerializer.Serialize(reply, Options);



   /// <summary>
   /// Reads a request's envelope and tells apart everything that can be wrong with it. Size is checked before anything is
   /// parsed. The version is checked before the other members, because a request of another version may rightly have
   /// members this one does not know. After that the envelope allows exactly <c>v</c>, <c>op</c>, <c>server</c> and
   /// <c>args</c>. Whether the operation exists, and whether it should name a server, is not decided here but by
   /// <see cref="ManagementOperations.Check"/>, and the arguments are read by <see cref="TryReadArguments"/>.
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
   /// Reads a request's arguments into the operation's own record, strictly. Arguments that were left out are read as
   /// an empty object, so a request without <c>args</c> is valid exactly when every member of the record is optional.
   /// It returns false when a member is unknown, repeated, missing or of the wrong kind, or when a value is refused by
   /// the type it belongs to (a session credential that is not a valid one, for instance); the caller answers
   /// <see cref="ManagementErrorCode.InvalidRequest"/>. What was wrong is not returned, because it may quote a secret.
   /// </summary>
   public static bool TryReadArguments<TArguments>(ManagementRequest request, [NotNullWhen(true)] out TArguments? arguments)
      where TArguments : class
   {
      ArgumentNullException.ThrowIfNull(request);
      arguments = Read<TArguments>(request.Args?.ToElement() ?? s_noArguments, StrictOptions);
      return arguments is not null;
   }



   /// <summary>Reads a successful reply's result into the operation's own record, skipping members this version does not know. It returns false when the reply has no result, or the result lacks a required member or otherwise does not fit the type.</summary>
   public static bool TryReadResult<TResult>(ManagementReply reply, [NotNullWhen(true)] out TResult? result)
      where TResult : class
   {
      ArgumentNullException.ThrowIfNull(reply);
      result = reply.Result is { } payload ? Read<TResult>(payload.ToElement(), Options) : null;
      return result is not null;
   }



   /// <summary>Reads an error's details into the type its code names, skipping members this version does not know. It returns false when the error has no details or they do not fit the type.</summary>
   public static bool TryReadDetails<TDetails>(ManagementError error, [NotNullWhen(true)] out TDetails? details)
      where TDetails : class
   {
      ArgumentNullException.ThrowIfNull(error);
      details = error.Details is { } payload ? Read<TDetails>(payload.ToElement(), Options) : null;
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



   /// <summary>Builds the serializer options. Both sets write the same text and are equally strict about repeated, missing and null members; they differ only in whether a reader skips a member it does not know.</summary>
   private static JsonSerializerOptions CreateOptions(bool skipUnknownMembers)
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
         AllowDuplicateProperties = false,
         UnmappedMemberHandling = skipUnknownMembers ? JsonUnmappedMemberHandling.Skip : JsonUnmappedMemberHandling.Disallow,
         RespectRequiredConstructorParameters = true,
         RespectNullableAnnotations = true,
         TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { LetNullableMembersBeAbsent } },
      };
      options.Converters.Add(new UtcDateTimeConverter());
      options.Converters.Add(new WriteOnlyConverter<ManagementRequest>(WriteRequest, nameof(ParseRequest)));
      options.Converters.Add(new WriteOnlyConverter<ManagementReply>(WriteReply, nameof(ParseReply)));
      options.Converters.Add(new WriteOnlyConverter<ManagementError>(WriteError, nameof(ParseReply)));
      options.Converters.Add(new WriteOnlyConverter<ManagementPayload>((writer, payload) => payload.ToElement().WriteTo(writer), nameof(ParseRequest) + " or " + nameof(ParseReply)));
      options.MakeReadOnly();
      return options;
   }



   /// <summary>
   /// Lets a member that may be null be absent. Such a member is left out of the text when it is null, so its absence
   /// must read back as null even where the record gives its constructor parameter no default (a nullable member that
   /// comes before a required one cannot have a default). A member that may not be null and has no default stays
   /// required; one with a default stays optional. A member its record marks as required, by the attribute or by the
   /// language's own keyword, stays required even when it may be null: there the record has said that the member must
   /// be written, null or not.
   /// </summary>
   private static void LetNullableMembersBeAbsent(JsonTypeInfo type)
   {
      foreach(JsonPropertyInfo member in type.Properties.Where(member => member.IsRequired && member.IsSetNullable && !IsMarkedRequired(member)))
      {
         member.IsRequired = false;
      }
   }



   /// <summary>
   /// True for a member whose declaration carries <see cref="JsonRequiredAttribute"/> or the <c>required</c> keyword.
   /// The declaration is always there to ask: the resolver these options use builds every member from a property of
   /// the record and records that property as the member's attribute provider.
   /// </summary>
   private static bool IsMarkedRequired(JsonPropertyInfo member)
      => member.AttributeProvider!.IsDefined(typeof(JsonRequiredAttribute), inherit: true) || member.AttributeProvider.IsDefined(typeof(RequiredMemberAttribute), inherit: true);



   /// <summary>
   /// Checks the size, parses the text and requires an object. On success the document is handed to the caller, who
   /// disposes it. Text the strict parse refuses is parsed once more with repeated members tolerated, only to learn
   /// whether a repeated member was the reason.
   /// </summary>
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
         return IsJsonWithARepeatedMember(text) ? ManagementEnvelopeProblem.RepeatedMember : ManagementEnvelopeProblem.NotJson;
      }
      if(parsed.RootElement.ValueKind != JsonValueKind.Object)
      {
         parsed.Dispose();
         return ManagementEnvelopeProblem.NotAnObject;
      }
      document = parsed;
      return ManagementEnvelopeProblem.None;
   }



   /// <summary>True when text the strict parse refused is well-formed JSON once a repeated member is tolerated, which means a repeated member is what was wrong with it.</summary>
   private static bool IsJsonWithARepeatedMember(string text)
   {
      try
      {
         using var tolerated = JsonDocument.Parse(text, s_repeatTolerantOptions);
         return true;
      }
      catch(JsonException)
      {
         return false;
      }
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



   /// <summary>Reads the members of a request object whose root is known to be an object and to repeat no member.</summary>
   private static ManagementRequestParse ReadRequest(JsonElement root)
   {
      ManagementEnvelopeProblem problem = CheckVersion(root);
      string? operation = null;
      string? server = null;
      ManagementPayload? arguments = null;
      foreach(JsonProperty member in root.EnumerateObject())
      {
         if(problem != ManagementEnvelopeProblem.None)
         {
            break;
         }
         JsonValueKind kind = member.Value.ValueKind;
         problem = member.Name switch
         {
            "v" => ManagementEnvelopeProblem.None,
            "op" => kind == JsonValueKind.String ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.OperationMissing,
            "server" => kind == JsonValueKind.String && ManagementProtocol.IsServerId(member.Value.GetString()) ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.ServerMalformed,
            "args" => kind == JsonValueKind.Object ? ManagementEnvelopeProblem.None : ManagementEnvelopeProblem.ArgumentsNotAnObject,
            _ => ManagementEnvelopeProblem.UnexpectedMember,
         };
         operation = member.NameEquals("op") ? member.Value.ToString() : operation;
         server = member.NameEquals("server") ? member.Value.ToString() : server;
         arguments = member.NameEquals("args") && kind == JsonValueKind.Object ? ManagementPayload.FromElement(member.Value) : arguments;
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
         true when ok.ValueKind == JsonValueKind.True && hasResult => new ManagementReply(ManagementProtocol.Version, true, ManagementPayload.FromElement(result)),
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
      ManagementPayload? details = error.TryGetProperty("details", out JsonElement found) && found.ValueKind == JsonValueKind.Object ? ManagementPayload.FromElement(found) : null;
      return ManagementError.FromWire(code.ToString(), message.ToString(), details);
   }



   /// <summary>
   /// Reads one element into a type, returning null for anything that does not fit it instead of throwing: a shape the
   /// serializer refuses, a value the type's own constructor refuses, or a list of the record's with a null in it. The
   /// serializer's own null checks do not reach inside a list, and no list in this contract may hold a null.
   /// </summary>
   private static TValue? Read<TValue>(JsonElement element, JsonSerializerOptions options)
      where TValue : class
   {
      try
      {
         return HasNullInAKnownList(element, typeof(TValue), options) ? null : element.Deserialize<TValue>(options);
      }
      catch(Exception exception) when(exception is JsonException or ArgumentException or FormatException or InvalidOperationException or NotSupportedException)
      {
         return null;
      }
   }



   /// <summary>
   /// True when a list the type knows, at any depth, has a null among its items. The element is walked beside the
   /// type the serializer will read it into, so only members of the record are looked at: a member this version does
   /// not know is skipped whole, as the serializer skips it, and what a later version puts in it (a list with a null
   /// included) cannot make today's reader refuse the reply.
   /// </summary>
   private static bool HasNullInAKnownList(JsonElement element, Type type, JsonSerializerOptions options)
   {
      JsonTypeInfo known = options.GetTypeInfo(type);
      return (known.Kind, element.ValueKind) switch
      {
         (JsonTypeInfoKind.Enumerable, JsonValueKind.Array) => element.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Null || HasNullInAKnownList(item, known.ElementType!, options)),
         (JsonTypeInfoKind.Object, JsonValueKind.Object) => element.EnumerateObject().Any(member => MemberType(known, member.Name) is { } memberType && HasNullInAKnownList(member.Value, memberType, options)),
         _ => false,
      };
   }



   /// <summary>The type of the record's member with this name on the wire, or null when the record has none.</summary>
   private static Type? MemberType(JsonTypeInfo record, string name) => record.Properties.FirstOrDefault(member => string.Equals(member.Name, name, StringComparison.Ordinal))?.PropertyType;



   /// <summary>Writes a request's envelope: the version, the operation, the server when one is named and the arguments when there are any.</summary>
   private static void WriteRequest(Utf8JsonWriter writer, ManagementRequest request)
   {
      writer.WriteStartObject();
      writer.WriteNumber("v", request.V);
      writer.WriteString("op", request.Op);
      if(request.Server is not null)
      {
         writer.WriteString("server", request.Server);
      }
      WritePayload(writer, "args", request.Args);
      writer.WriteEndObject();
   }



   /// <summary>Writes a reply's envelope: the version, whether the operation succeeded, and the result or the error, whichever the reply holds.</summary>
   private static void WriteReply(Utf8JsonWriter writer, ManagementReply reply)
   {
      writer.WriteStartObject();
      writer.WriteNumber("v", reply.V);
      writer.WriteBoolean("ok", reply.Ok);
      WritePayload(writer, "result", reply.Result);
      if(reply.Error is not null)
      {
         writer.WritePropertyName("error");
         WriteError(writer, reply.Error);
      }
      writer.WriteEndObject();
   }



   /// <summary>Writes an error: its code, its message and its details when it has any.</summary>
   private static void WriteError(Utf8JsonWriter writer, ManagementError error)
   {
      writer.WriteStartObject();
      writer.WriteString("code", error.Code);
      writer.WriteString("message", error.Message);
      WritePayload(writer, "details", error.Details);
      writer.WriteEndObject();
   }



   /// <summary>Writes a payload as a member of the object being written, or nothing when there is none.</summary>
   private static void WritePayload(Utf8JsonWriter writer, string name, ManagementPayload? payload)
   {
      if(payload is not null)
      {
         writer.WritePropertyName(name);
         payload.ToElement().WriteTo(writer);
      }
   }



   /// <summary>
   /// Writes every date and time as ISO 8601 in UTC with a trailing Z, whatever offset the value had, and reads back only
   /// ISO 8601 that says which instant it means: with a trailing Z or an offset. A date and time without one, a date
   /// alone and any other notation are refused, so two computers in different time zones cannot read one text as two
   /// instants.
   /// </summary>
   private sealed class UtcDateTimeConverter : JsonConverter<DateTimeOffset>
   {
      /// <summary>Reads an ISO 8601 date and time that ends in Z or in an offset, as UTC.</summary>
      public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
      {
         /////////////////////////////////////////////////////////////////////////////////////////
         // The serializer never hands a null to this converter: it reads a null itself for a  //
         // member that may be null and refuses it for one that may not. Any token but text    //
         // makes GetString throw, which the caller treats as a value that does not fit.       //
         /////////////////////////////////////////////////////////////////////////////////////////
         string text = reader.GetString()!;
         bool namesItsOffset = text.EndsWith('Z') || (text.Length > 6 && text[^6] is '+' or '-' && text[^3] == ':');
         return namesItsOffset && reader.TryGetDateTimeOffset(out DateTimeOffset value)
            ? value.ToUniversalTime() : throw new JsonException("A date and time must be ISO 8601 with a trailing Z or an offset.");
      }



      /// <summary>Writes the instant in UTC with seven fractional digits and a trailing Z.</summary>
      public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
         => writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
   }



   /// <summary>
   /// Writes one of the envelope's types and refuses to read it. The serializer would read an envelope without any of
   /// the checks <see cref="ParseRequest"/> and <see cref="ParseReply"/> make (another version, a malformed operation
   /// name, a member the envelope does not have), so reading through it is closed and the refusal names the function
   /// to use instead.
   /// </summary>
   private sealed class WriteOnlyConverter<TEnvelope>(Action<Utf8JsonWriter, TEnvelope> write, string readWith) : JsonConverter<TEnvelope>
   {
      /// <summary>Refuses, naming the function that reads this type with its checks.</summary>
      public override TEnvelope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
         => throw new NotSupportedException($"A {typeof(TEnvelope).Name} is not read by the serializer. Read it with {nameof(ManagementJson)}.{readWith}, which checks the envelope.");



      /// <summary>Writes the value the one way the wire carries it.</summary>
      public override void Write(Utf8JsonWriter writer, TEnvelope value, JsonSerializerOptions options) => write(writer, value);
   }
}
