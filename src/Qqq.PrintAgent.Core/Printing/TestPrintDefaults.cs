namespace Qqq.PrintAgent.Core.Printing;

/// <summary>Suggested diagnostic defaults; the operator must still verify installed stock and resolution.</summary>
public static class TestPrintDefaults
{
   public static string Format(IEnumerable<string> supportedFormats)
      => supportedFormats.Contains("ZPL", StringComparer.Ordinal) ? "ZPL" : "PDF";

   public static string Profile(string format, string? driverName)
   {
      if(!string.Equals(format, "ZPL", StringComparison.Ordinal))
      {
         return "LETTER";
      }
      string driver = driverName ?? string.Empty;
      if(driver.Contains("600dpi", StringComparison.OrdinalIgnoreCase) || driver.Contains("600 dpi", StringComparison.OrdinalIgnoreCase))
      {
         return "LABEL_4X6_600";
      }
      return driver.Contains("300dpi", StringComparison.OrdinalIgnoreCase) || driver.Contains("300 dpi", StringComparison.OrdinalIgnoreCase)
         ? "LABEL_4X6_300" : "LABEL_4X6_203";
   }
}
