using System;
using System.Globalization;

namespace SaturdayPulse.Helpers
{
    public static class StringDateExtensions
    {
        // Extension for safe parsing (returns null if invalid)
        public static DateTime? ToDateTime(this string value, string format = null)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            // If a specific format is provided, use ParseExact
            if (!string.IsNullOrEmpty(format))
            {
                if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exactResult))
                {
                    return exactResult;
                }
                return null;
            }

            // Default to standard culture-aware parsing
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result))
            {
                return result;
            }

            return null;
        }

        public static string ToDisplayDate(this string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime exactResult))
            {
                return exactResult.ToString("ddd, MMM %d", CultureInfo.InvariantCulture);
            }

            return value;
        }
    }
}
