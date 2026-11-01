/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Server policy, cached locally; platform ceilings are advertised separately and never silently clamped.</summary>
public sealed record RealtimeSettings(
   long Revision = 0, long MaxPdfBytes = 104857600, long MaxZplBytes = 20971520,
   int MaxFrameBytes = 1048576, int MaxQueuedFrames = 64, long MaxQueuedFrameBytes = 4194304,
   int MaxConcurrentDownloads = 1, int MaxConcurrentPrints = 1, long MaxStagedBytes = 536870912,
   long MaxDownloadBytesPerSecond = 0, int RequestTimeoutMillis = 30000,
   int DownloadConnectTimeoutMillis = 30000, int WebSocketHandshakeTimeoutMillis = 30000,
   int AckTimeoutMillis = 15000, int DownloadIdleTimeoutMillis = 30000, int DownloadTimeoutMillis = 600000,
   int HeartbeatIntervalSeconds = 20, int HeartbeatTimeoutSeconds = 60,
   int ReconnectMinDelayMillis = 1000, int ReconnectMaxDelayMillis = 30000, int ReconnectJitterPercent = 20, int MaxCopies = 100,
   int NativeSubmissionTimeoutMillis = 120000)
{
   private static readonly HashSet<string> RequiredFields = JsonSerializer.SerializeToElement(new RealtimeSettings(), PrintApiClient.JsonOptions)
      .EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);



   /// <summary>
   /// Reads the server's settings object strictly: it must be an object whose fields are exactly the known set, each appearing
   /// once with an integer value, with none missing, and the values must pass <c>Validate</c>. An incomplete, extended or
   /// out-of-range policy is refused rather than partly applied or silently reduced.
   /// </summary>
   public static RealtimeSettings Parse(JsonElement value)
   {
      if(value.ValueKind != JsonValueKind.Object)
      { throw new InvalidDataException("Realtime settings must be an object."); }
      var seen = new HashSet<string>(StringComparer.Ordinal);
      foreach(JsonProperty property in value.EnumerateObject())
      {
         if(!RequiredFields.Contains(property.Name) || !seen.Add(property.Name) || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out _))
         { throw new InvalidDataException("Realtime settings contain an unknown, duplicate or non-integer field."); }
      }
      if(!seen.SetEquals(RequiredFields))
      { throw new InvalidDataException("The server omitted a required realtime setting."); }
      RealtimeSettings settings = value.Deserialize<RealtimeSettings>(PrintApiClient.JsonOptions) ?? throw new InvalidDataException("Missing realtime settings.");
      settings.Validate();
      return settings;
   }



   /// <summary>The ceilings this client can honor, sent in HELLO so the server never offers a policy larger than the agent supports; the agent rejects such a policy instead of clamping it.</summary>
   public static object ClientLimits => new
   {
      maxPdfBytes = int.MaxValue,
      maxZplBytes = int.MaxValue,
      maxFrameBytes = 16777216,
      maxQueuedFrames = 4096,
      maxQueuedFrameBytes = 67108864,
      maxConcurrentDownloads = 16,
      maxConcurrentPrints = 16,
      maxStagedBytes = 9007199254740991L,
      maxCopies = 32767,
   };



   /// <summary>The largest document allowed for a format (PDF or ZPL); any other format is an error, so an unknown type can never bypass the size limit.</summary>
   public long MaximumDocumentBytes(string format) => format switch
   {
      "PDF" => MaxPdfBytes,
      "ZPL" => MaxZplBytes,
      _ => throw new InvalidDataException("Unsupported document format."),
   };



   /// <summary>
   /// Requires every limit to be inside the range this client supports and consistent with the others: the queue holds at least
   /// one frame, staging fits the largest document, the download limit is not shorter than the idle or connect timeout, and the
   /// heartbeat timeout is at least twice the interval. A policy that fails is a configuration error the operator must resolve.
   /// </summary>
   public void Validate()
   {
      if(Revision is < 0 or > 9007199254740991L || NativeSubmissionTimeoutMillis is < 1000 or > 86400000 || MaxCopies is < 1 or > 32767 || MaxPdfBytes is < 1 or > int.MaxValue || MaxZplBytes is < 1 or > int.MaxValue
         || MaxFrameBytes is < 8192 or > 16777216 || MaxQueuedFrames is < 8 or > 4096
         || MaxQueuedFrameBytes < MaxFrameBytes || MaxQueuedFrameBytes > 67108864
         || MaxConcurrentDownloads is < 1 or > 16 || MaxConcurrentPrints is < 1 or > 16
         || MaxStagedBytes < Math.Max(MaxPdfBytes, MaxZplBytes) || MaxStagedBytes > 9007199254740991L
         || MaxDownloadBytesPerSecond is < 0 or > int.MaxValue
         || !RequestTimeout(RequestTimeoutMillis) || !RequestTimeout(DownloadConnectTimeoutMillis)
         || !RequestTimeout(WebSocketHandshakeTimeoutMillis) || !RequestTimeout(AckTimeoutMillis)
         || DownloadIdleTimeoutMillis is < 1000 or > 3600000 || DownloadTimeoutMillis is < 1000 or > 86400000
         || DownloadTimeoutMillis < Math.Max(DownloadIdleTimeoutMillis, DownloadConnectTimeoutMillis)
         || HeartbeatIntervalSeconds is < 5 or > 300 || HeartbeatTimeoutSeconds is < 10 or > 900
         || HeartbeatTimeoutSeconds < 2 * HeartbeatIntervalSeconds
         || ReconnectMinDelayMillis is < 100 or > 60000 || ReconnectMaxDelayMillis is < 1000 or > 300000
         || ReconnectMaxDelayMillis < ReconnectMinDelayMillis || ReconnectJitterPercent is < 0 or > 50)
      {
         throw new InvalidDataException("CONFIGURATION_REQUIRED: the realtime policy exceeds the advertised client limits or is inconsistent.");
      }
   }



   /// <summary>True for a timeout between one second and five minutes, the range allowed for request-style timeouts.</summary>
   private static bool RequestTimeout(int value) => value is >= 1000 and <= 300000;
}
