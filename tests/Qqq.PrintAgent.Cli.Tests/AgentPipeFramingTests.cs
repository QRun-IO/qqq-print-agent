using System.IO.Pipes;
using System.Text;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentPipeFramingTests
{
   [Fact]
   public async Task Read_RejectsOversizedUnterminatedInputBeforeReadingWholeStream()
   {
      using MemoryStream input = new(Encoding.UTF8.GetBytes(new string('x', 100_000)));
      await Assert.ThrowsAsync<InvalidDataException>(() => AgentPipeFraming.ReadAsync(input, 100, CancellationToken.None));
      Assert.True(input.Position < input.Length);
   }

   [Fact]
   public async Task Read_RequiresCompleteFrame_AndPreservesUnicodeAcrossBuffers()
   {
      string text = new string('x', 4095) + "☃😀\r\n";
      using MemoryStream input = new(Encoding.UTF8.GetBytes(text));
      Assert.Equal(text.TrimEnd('\r', '\n'), await AgentPipeFraming.ReadAsync(input, 5000, CancellationToken.None));
      using MemoryStream incomplete = new(Encoding.UTF8.GetBytes("{\"status\":\"success\"}"));
      await Assert.ThrowsAsync<IOException>(() => AgentPipeFraming.ReadAsync(incomplete, 100, CancellationToken.None));
   }

   [Fact]
   public async Task Write_CancelsWhenConnectedClientDoesNotRead()
   {
      string name = "qqq-framing-" + Guid.NewGuid().ToString("N");
      using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
      await using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      Task connect = client.ConnectAsync(deadline.Token);
      await server.WaitForConnectionAsync(deadline.Token);
      await connect;
      using CancellationTokenSource writeTimeout = new(TimeSpan.FromMilliseconds(100));
      Task write = AgentPipeFraming.WriteAsync(server, new string('x', 4 * 1024 * 1024), AgentPipeFraming.MaximumResponseCharacters, writeTimeout.Token);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(deadline.Token));
      Assert.True(write.IsCanceled);
   }

   [Fact]
   public async Task Read_CancelsWhenConnectedClientNeverTerminatesCommand()
   {
      string name = "qqq-framing-" + Guid.NewGuid().ToString("N");
      using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
      await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
      await using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
      Task connect = client.ConnectAsync(deadline.Token);
      await server.WaitForConnectionAsync(deadline.Token);
      await connect;
      using CancellationTokenSource readTimeout = new(TimeSpan.FromMilliseconds(100));
      Task<string> read = AgentPipeFraming.ReadAsync(server, 100, readTimeout.Token);
      await client.WriteAsync(Encoding.UTF8.GetBytes("unfinished"), deadline.Token);
      await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(deadline.Token));
      Assert.True(read.IsCanceled);
   }
}
