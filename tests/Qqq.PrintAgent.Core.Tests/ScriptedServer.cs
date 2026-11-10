/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>What the scripted server saw for one request: method, address, body and the credential-bearing headers, so a test can prove where each secret was and was not sent.</summary>
internal sealed record ScriptedRequest(HttpMethod Method, Uri Uri, string? Body, string? Authorization, string? Cookie, string? DeviceId, string? DeviceToken, string? ClientChannel, string? Origin)
{
   /// <summary>True when the request carried no credential of any kind, as every request made before sign-in must.</summary>
   internal bool IsPublic => Authorization is null && Cookie is null && DeviceId is null && DeviceToken is null;
}



/// <summary>
/// A hand-written fake QQQ server for the session client: it records every request and answers them in the order replies
/// were queued, so a test fixes the exact conversation and fails when the client deviates. Nothing leaves the process.
/// </summary>
internal sealed class ScriptedServer : HttpMessageHandler
{
   internal const string Discovery = """{"apiVersion":"1.10.0","features":["version","metaData","processes","processCancel","extensions","widgets","nativeSessionLogin","nativeSessionLogout"],"clientChannels":["api"],"extensions":[{"name":"printing","version":"2.4.0","pathPrefix":"/qbit/printing"}]}""";
   internal const string Table = """{"name":"local","type":"TABLE_BASED"}""";
   internal const string Identity = """{"principalId":"user-1","tenantId":"tenant-1","deviceId":"device-1"}""";
   internal const string Issuer = "https://id.example.invalid/application/";
   internal const string Callback = "io.qrun.qqq-print-agent:/oauth/callback";
   internal const string OAuth = """{"name":"configured-provider","type":"OAUTH2","values":{"clientId":"qqq-client","scopes":"openid profile","nativeSessionLogin":{"flow":"AUTHORIZATION_CODE_PKCE_SESSION","codeChallengeMethod":"S256","prompt":"login","issuer":"https://id.example.invalid/application/","discoveryUrl":"https://id.example.invalid/application/.well-known/openid-configuration","redirectUris":["io.qrun.qqq-print-agent:/oauth/callback"]},"nativeSessionLogout":{"flow":"OIDC_RP_INITIATED_LOGOUT","postLogoutRedirectUris":["io.qrun.qqq-print-agent:/oauth/callback"]}}}""";
   internal const string Provider = """{"issuer":"https://id.example.invalid/application/","authorization_endpoint":"https://id.example.invalid/authorize","end_session_endpoint":"https://id.example.invalid/end-session","code_challenge_methods_supported":["S256"]}""";
   internal static readonly Uri Origin = new("https://printing.example.invalid");
   private readonly Queue<Func<CancellationToken, Task<HttpResponseMessage>>> _responses = new();



   /// <summary>Every request received, in order.</summary>
   internal List<ScriptedRequest> Requests { get; } = [];



   /// <summary>A server that has already queued discovery and table-based authentication, the two public replies before a password sign-in.</summary>
   internal static ScriptedServer WithTable() => new ScriptedServer().Add(Discovery).Add(Table);



   /// <summary>A server whose first three replies take a password sign-in to a held session named "session".</summary>
   internal static ScriptedServer SignedIn() => WithTable().Add("""{"uuid":"session"}""");



   /// <summary>A server that has already queued discovery and the OAuth2 native-session description, the two public replies before a browser sign-in.</summary>
   internal static ScriptedServer WithOAuth() => new ScriptedServer().Add(Discovery).Add(OAuth);



   /// <summary>Queues a JSON reply with the given status and optional Set-Cookie header.</summary>
   internal ScriptedServer Add(string body, HttpStatusCode status = HttpStatusCode.OK, string? cookie = null) => AddResponse(() => Json(body, status, cookie));



   /// <summary>Queues a reply built on demand, for responses that need custom headers or content.</summary>
   internal ScriptedServer AddResponse(Func<HttpResponseMessage> response) => AddAsync(_ => Task.FromResult(response()));



   /// <summary>Queues a request that fails before any response, as a refused connection or a failed handshake does.</summary>
   internal ScriptedServer AddFailure(Func<Exception> failure) => AddAsync(_ => Task.FromException<HttpResponseMessage>(failure()));



   /// <summary>Queues an asynchronous reply, for responses a test holds open and releases later.</summary>
   internal ScriptedServer AddAsync(Func<CancellationToken, Task<HttpResponseMessage>> response)
   {
      _responses.Enqueue(response);
      return this;
   }



   /// <summary>A JSON response with the given status, optionally carrying a Set-Cookie header.</summary>
   internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? cookie = null)
   {
      var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
      if(cookie is not null)
      { response.Headers.Add("Set-Cookie", cookie); }
      return response;
   }



   /// <summary>Records the request, including its body and credential headers, then returns the next queued reply; a request nobody scripted fails the test.</summary>
   protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
   {
      string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
      string? Header(string name) => request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join("; ", values) : null;
      Requests.Add(new ScriptedRequest(request.Method, request.RequestUri!, body, request.Headers.Authorization?.ToString(), Header("Cookie"),
         Header("X-QQQ-Device-Id"), Header("X-QQQ-Device-Token"), Header("X-QQQ-Client"), Header("Origin")));
      Assert.True(_responses.Count > 0, $"The client sent an unscripted request: {request.Method} {request.RequestUri!.AbsolutePath}");
      return await _responses.Dequeue()(cancellationToken);
   }
}
