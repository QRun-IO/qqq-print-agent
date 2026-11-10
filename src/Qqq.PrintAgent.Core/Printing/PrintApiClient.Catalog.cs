/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Net.Http.Json;

namespace Qqq.PrintAgent.Core.Printing;

/// <summary>
/// Catalog part of the API client: reads the operator printer catalog so the agent can tell which of its shared printers the
/// server has approved. The device-facing routes never report approval, and the catalog is the one authenticated view a
/// signed-in user has of it.
/// </summary>
public sealed partial class PrintApiClient
{
   /// <summary>Catalog page size; the contract allows 1 to 200.</summary>
   internal const int CatalogPageSize = 200;

   /// <summary>Upper bound on catalog pages read in one call, so a server that keeps returning cursors cannot keep the agent busy.</summary>
   internal const int CatalogPageLimit = 10;



   /// <summary>
   /// Reads the whole operator printer catalog (every page until the server returns no cursor, at most ten pages). The catalog is
   /// user-scoped, and it is the signed-in session's user who reads it. A 404 means the server does not offer the catalog, a 401
   /// or 403 that the signed-in user may not read it; both are reported as such rather than hidden.
   /// </summary>
   public async Task<IReadOnlyList<OperatorPrinter>> OperatorPrintersAsync(CancellationToken token)
   {
      List<OperatorPrinter> printers = [];
      string? cursor = null;
      for(int page = 0; page < CatalogPageLimit; page++)
      {
         string path = "operator/printers?limit=" + CatalogPageSize + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor));
         using HttpResponseMessage response = await _http.GetAsync(path, token).ConfigureAwait(false);
         if(response.StatusCode == HttpStatusCode.NotFound)
         {
            throw new NotSupportedException("The server does not offer the operator printer catalog (printing extension 2.3 or later).");
         }
         if(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
         {
            throw new UnauthorizedAccessException($"The signed-in user may not read the operator printer catalog (HTTP {(int)response.StatusCode}).");
         }
         response.EnsureSuccessStatusCode();
         OperatorPrinterList result = await response.Content.ReadFromJsonAsync<OperatorPrinterList>(JsonOptions, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Missing operator printer catalog.");
         printers.AddRange(result.Printers ?? []);
         cursor = result.NextCursor;
         if(string.IsNullOrEmpty(cursor))
         {
            return printers;
         }
      }
      throw new InvalidDataException($"The operator printer catalog did not end after {CatalogPageLimit} pages.");
   }



   /// <summary>One catalog page: its printers and the cursor for the next page, or null at the end.</summary>
   private sealed record OperatorPrinterList(OperatorPrinter[]? Printers, string? NextCursor);
}
