/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;

namespace Qqq.PrintAgent.Windows;

/// <summary>Bounded, cancellable framing for one-command local pipe connections.</summary>
public static class AgentPipeFraming
{
   public const int MaximumCommandCharacters = 65_536;
   public const int MaximumResponseCharacters = 8 * 1024 * 1024;

   public static async Task<string> ReadAsync(Stream stream, int maximumCharacters, CancellationToken token)
   {
      ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
      using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
      StringBuilder result = new();
      char[] buffer = new char[4096];
      while(true)
      {
         int read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
         if(read == 0)
         {
            throw new IOException("Service connection closed before a complete response was received.");
         }
         int newline = Array.IndexOf(buffer, '\n', 0, read);
         int count = newline < 0 ? read : newline;
         if(result.Length + count > maximumCharacters)
         {
            throw new InvalidDataException("Local pipe message exceeds the permitted size.");
         }
         result.Append(buffer, 0, count);
         if(newline >= 0)
         {
            if(result.Length > 0 && result[^1] == '\r')
            {
               result.Length--;
            }
            return result.ToString();
         }
      }
   }

   public static async Task WriteAsync(Stream stream, string message, int maximumCharacters, CancellationToken token)
   {
      if(message.Length > maximumCharacters)
      {
         throw new InvalidDataException("Local pipe message exceeds the permitted size.");
      }
      // Write directly: disposing a buffered writer must never retry a blocked flush.
      byte[] bytes = Encoding.UTF8.GetBytes(message + "\n");
      await stream.WriteAsync(bytes, token).ConfigureAwait(false);
   }
}
