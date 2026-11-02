/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.IO.Pipes;
using System.Text;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The management pipe exchanges one newline-terminated message per command. These tests hold the framing to its limits: bounded size, complete frames only, correct Unicode, and no waiting forever on a client that stalls.</summary>
public sealed class AgentPipeFramingTests
{
   /// <summary>Reading with a 100-character limit from a 100,000-character stream with no newline fails with invalid data after reading only a small part of it, so an oversized message cannot make the service consume the whole stream.</summary>
   [Fact]
   public async Task Read_RejectsOversizedUnterminatedInputBeforeReadingWholeStream()
   {
      using MemoryStream input = new(Encoding.UTF8.GetBytes(new string('x', 100_000)));
      await Assert.ThrowsAsync<InvalidDataException>(() => AgentPipeFraming.ReadAsync(input, 100, CancellationToken.None));
      Assert.True(input.Position < input.Length);
   }



   /// <summary>A message that crosses the 4,096-character read buffer and contains multi-byte and surrogate-pair characters comes back intact with its line ending removed; a message that ends without a newline is an I/O error, never silently accepted as complete.</summary>
   [Fact]
   public async Task Read_RequiresCompleteFrame_AndPreservesUnicodeAcrossBuffers()
   {
      string text = new string('x', 4095) + "☃😀\r\n";
      using MemoryStream input = new(Encoding.UTF8.GetBytes(text));
      Assert.Equal(text.TrimEnd('\r', '\n'), await AgentPipeFraming.ReadAsync(input, 5000, CancellationToken.None));
      using MemoryStream incomplete = new(Encoding.UTF8.GetBytes("{\"status\":\"success\"}"));
      await Assert.ThrowsAsync<IOException>(() => AgentPipeFraming.ReadAsync(incomplete, 100, CancellationToken.None));
   }



   /// <summary>Writing a 4 MiB reply to a connected client that never reads stops when its token is cancelled (after 100 ms here) instead of blocking the service indefinitely.</summary>
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



   /// <summary>A client that sends part of a command and never ends the line does not hold the service: the read is cancelled by its own deadline (100 ms here) rather than waiting for the rest.</summary>
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
