using System.Net;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class DeviceControlApiTests
{
   [Fact]
   public async Task SharedControlFixtureIsAcceptedAndCapabilitiesAdvertised()
   {
      string body = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "control-poll.response.json"));
      using Handler handler = new(body);
      using PrintApiClient api = new("https://example.invalid", "fixture", handler);
      DeviceControlPoll response = (await api.ControlPollAsync(0, CancellationToken.None))!;
      Assert.True(response.DesiredState.DispatchPaused);
      Assert.Equal(2, response.Commands.Count);
      using var request = JsonDocument.Parse(handler.RequestBody!);
      Assert.Equal(1, request.RootElement.GetProperty("controlVersion").GetInt32());
      Assert.Equal(2, request.RootElement.GetProperty("capabilities").GetArrayLength());
   }

   [Theory]
   [InlineData("{}")]
   [InlineData("null")]
   [InlineData("{\"controlVersion\":2}")]
   public async Task MissingOrInvalidControlStateNeverMeansConnected(string body)
   {
      using PrintApiClient api = new("https://example.invalid", "fixture", new Handler(body));
      await Assert.ThrowsAsync<InvalidDataException>(() => api.ControlPollAsync(0, CancellationToken.None));
   }

   private sealed class Handler(string body) : HttpMessageHandler
   {
      internal string? RequestBody { get; private set; }
      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
      }
   }
}
