/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Net;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Core.Tests;

public sealed class TestPrintProfileTests
{
   [Theory]
   [InlineData("PDF", "LETTER")]
   [InlineData("PDF", "A4")]
   [InlineData("PDF", "LABEL_4X6_300")]
   [InlineData("ZPL", "LABEL_4X6_203")]
   [InlineData("ZPL", "LABEL_4X6_300")]
   [InlineData("ZPL", "LABEL_4X6_600")]
   [InlineData("PDF", "LABEL_4X2_300")]
   [InlineData("PDF", "LABEL_4X1_300")]
   [InlineData("ZPL", "LABEL_4X2_203")]
   [InlineData("ZPL", "LABEL_4X1_300")]
   [InlineData("ZPL", "LABEL_4X1_600")]
   public async Task SelectedMediaProfile_IsIncludedInActualTestPrintRequest(string format, string profile)
   {
      using CaptureHandler handler = new();
      using PrintApiClient api = new("https://synthetic.invalid", "synthetic-token", handler);
      string job = await api.OperatorActionAsync("test-print", null, "queue-1", format, false, profile, CancellationToken.None);
      Assert.Equal("new-test-job", job);
      Assert.Equal("/printing/v1/device/test-print", handler.Path);
      JsonElement body = handler.Body;
      Assert.Equal("queue-1", body.GetProperty("printerId").GetString());
      Assert.Equal(format, body.GetProperty("format").GetString());
      Assert.Equal(profile, body.GetProperty("testProfile").GetString());
      Assert.True(Guid.TryParseExact(body.GetProperty("requestId").GetString(), "N", out _));
   }

   [Fact]
   public async Task ExistingCaller_KeepsLegacyShapeWithoutNullTestProfile()
   {
      using CaptureHandler handler = new();
      using PrintApiClient api = new("https://synthetic.invalid", "synthetic-token", handler);
      await api.OperatorActionAsync("test-print", null, "queue-1", "PDF", false, CancellationToken.None);
      Assert.False(handler.Body.TryGetProperty("testProfile", out _));
      Assert.Equal(4, handler.Body.EnumerateObject().Count());
   }

   [Theory]
   [InlineData("retry-job")]
   [InlineData("cancel-job")]
   public async Task Profile_IsNeverSentForOtherOperatorActions(string operation)
   {
      using CaptureHandler handler = new();
      using PrintApiClient api = new("https://synthetic.invalid", "synthetic-token", handler);
      await api.OperatorActionAsync(operation, "existing-job", null, null, true, "LABEL_4X6_600", CancellationToken.None);
      Assert.False(handler.Body.TryGetProperty("testProfile", out _));
      Assert.True(handler.Body.GetProperty("confirmDuplicateRisk").GetBoolean());
   }

   [Fact]
   public void LabelCapability_TakesPrecedenceOverPdfEnumerationOrder()
   {
      Assert.Equal("ZPL", TestPrintDefaults.Format(["PDF", "ZPL"]));
      Assert.Equal("ZPL", TestPrintDefaults.Format(["ZPL"]));
      Assert.Equal("PDF", TestPrintDefaults.Format(["PDF"]));
   }

   [Theory]
   [InlineData("ZPL", "ZDesigner ZT411-300dpi", "LABEL_4X6_300")]
   [InlineData("ZPL", "Thermal 600 DPI", "LABEL_4X6_600")]
   [InlineData("ZPL", "Generic thermal", "LABEL_4X6_203")]
   [InlineData("ZPL", "Model 600 with unknown resolution", "LABEL_4X6_203")]
   [InlineData("PDF", "Laser 600dpi", "LETTER")]
   public void ResolutionSuggestion_UsesExplicitDpiHintsOnly(string format, string driver, string expected)
      => Assert.Equal(expected, TestPrintDefaults.Profile(format, driver));

   [Fact]
   public void ShortLabelChoices_AreFriendlyAndKeepFourBySixFirst()
   {
      IReadOnlyList<(string Label, string Profile)> zpl = TestPrintDefaults.Choices("ZPL");
      Assert.Equal(
         new[] { "LABEL_4X6_203", "LABEL_4X6_300", "LABEL_4X6_600", "LABEL_4X2_203", "LABEL_4X2_300", "LABEL_4X2_600", "LABEL_4X1_203", "LABEL_4X1_300", "LABEL_4X1_600" },
         zpl.Select(choice => choice.Profile));
      Assert.Equal("4 × 6 in label — 203 dpi", zpl[0].Label);
      Assert.Equal("4 × 1 in label — 600 dpi", zpl[^1].Label);
      IReadOnlyList<(string Label, string Profile)> pdf = TestPrintDefaults.Choices("PDF");
      Assert.Equal(new[] { "LETTER", "A4", "LABEL_4X6_300", "LABEL_4X2_300", "LABEL_4X1_300" }, pdf.Select(choice => choice.Profile));
      Assert.Equal("4 × 6 in label", pdf[2].Label);
      Assert.All(zpl.Concat(pdf), choice => Assert.DoesNotContain("LABEL_", choice.Label, StringComparison.Ordinal));
   }

   [Theory]
   [InlineData("LABEL_4X6_300", "4 × 6 in")]
   [InlineData("LABEL_4X2_203", "4 × 2 in")]
   [InlineData("LABEL_4X1_600", "4 × 1 in")]
   [InlineData("LETTER", "")]
   [InlineData(null, "")]
   public void LabelStock_NamesTheStockToLoad(string? profile, string expected)
      => Assert.Equal(expected, TestPrintDefaults.LabelStock(profile));

   private sealed class CaptureHandler : HttpMessageHandler
   {
      internal JsonElement Body { get; private set; }
      internal string? Path { get; private set; }

      protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
      {
         Path = request.RequestUri!.AbsolutePath;
         using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
         Body = document.RootElement.Clone();
         return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"jobId\":\"new-test-job\"}") };
      }
   }
}
