using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http.Headers;

namespace helpers 
{
    public static class WxsWeatherReader
    {
        public sealed class WeatherRow
        {
            public int Year { get; init; }
            public int Month { get; init; }
            public int Day { get; init; }
            public int Hour { get; init; }
            public int Minute { get; init; }

            public double TempC { get; init; }
            public double RH { get; init; }                // percent (0-100)
            public double HourlyPcpCm { get; init; }       // as in file, assumed cm for your usage
            public double WindSpd { get; init; }           // whatever units file uses (often km/h for “METRIC”)
            public double WindDirDeg { get; init; }
            public int CloudCov { get; init; }          // percent (0-100), if present

            public double RhFrac => RH / 100.0;
        }

        /// <summary>
        /// Reads a .wxs weather file and returns rows suitable for driving initializeEnvironment.
        /// solarModel: optional callback to compute SolarWm2 per row (e.g., from cloud cover + time).
        /// If null, SolarWm2 is set to double.NaN.
        /// </summary>
        public static List<WeatherRow> ReadWxs(string wxsPath, Func<WeatherRow, double>? solarModel = null)
        {
            if (string.IsNullOrWhiteSpace(wxsPath))
                throw new ArgumentException("wxsPath is required.", nameof(wxsPath));

            if (!File.Exists(wxsPath))
                throw new FileNotFoundException("WXS file not found.", wxsPath);

            var rows = new List<WeatherRow>(capacity: 4096);
            var inv = CultureInfo.InvariantCulture;

            bool sawColumnHeader = false;
            double cumRain = 0.0;

            foreach (var rawLine in File.ReadLines(wxsPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                // Skip metadata/header lines
                if (line.StartsWith("RAWS_", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Column header line
                if (!sawColumnHeader)
                {
                    // Expect something like: "Year Mth Day Time Temp RH HrlyPcp WindSpd WindDir CloudCov"
                    if (line.StartsWith("Year", StringComparison.OrdinalIgnoreCase))
                    {
                        sawColumnHeader = true;
                        continue;
                    }
                    else
                    {
                        // If file has extra preface lines, ignore until we hit the header.
                        continue;
                    }
                }

                // Data line: whitespace-separated
                var parts = SplitWhitespace(line);
                if (parts.Count < 6)
                    continue; // or throw; depends on how strict you want

                // Expected order:
                // 0 Year, 1 Mth, 2 Day, 3 Time(HHMM), 4 Temp, 5 RH, 6 HrlyPcp, 7 WindSpd, 8 WindDir, 9 CloudCov (optional)
                int year = int.Parse(parts[0], inv);
                int month = int.Parse(parts[1], inv);
                int day = int.Parse(parts[2], inv);

                ParseTimeHHMM(parts[3], out int hour, out int minute);

                double temp = double.Parse(parts[4], inv);
                double rh = double.Parse(parts[5], inv);

                double pcp = parts.Count > 6 ? double.Parse(parts[6], inv) : 0.0;
                double wspd = parts.Count > 7 ? double.Parse(parts[7], inv) : 0.0;
                double wdir = parts.Count > 8 ? double.Parse(parts[8], inv) : 0.0;
                int cloud = parts.Count > 9 ? int.Parse(parts[9], inv) : 0;

                cumRain += pcp;

                var row = new WeatherRow
                {
                    Year = year,
                    Month = month,
                    Day = day,
                    Hour = hour,
                    Minute = minute,
                    TempC = temp,
                    RH = rh,
                    HourlyPcpCm = pcp,
                    WindSpd = wspd,
                    WindDirDeg = wdir,
                    CloudCov = cloud
                };
                rows.Add(row);
            }

            if (!sawColumnHeader)
                throw new InvalidDataException("Did not find expected WXS column header line starting with 'Year'.");

            return rows;
        }

        private static void ParseTimeHHMM(string hhmm, out int hour, out int minute)
        {
            // Handles "0000", "1300", etc.
            // Also tolerates "0" or "100" style if it ever occurs.
            hhmm = hhmm.Trim();

            if (hhmm.Length <= 2)
            {
                hour = int.Parse(hhmm, CultureInfo.InvariantCulture);
                minute = 0;
                return;
            }

            // pad to 4 chars
            if (hhmm.Length < 4) hhmm = hhmm.PadLeft(4, '0');

            hour = int.Parse(hhmm.Substring(0, 2), CultureInfo.InvariantCulture);
            minute = int.Parse(hhmm.Substring(2, 2), CultureInfo.InvariantCulture);
        }

        private static List<string> SplitWhitespace(string s)
        {
            var result = new List<string>(16);
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                if (i >= s.Length) break;
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                result.Add(s.Substring(start, i - start));
            }
            return result;
        }

        public static double getEMC(double rh, double temp)
        {
            temp = 32 + temp * 9 / 5;
            if (rh < 11) return 0.03299 + 0.281073*rh - 0.000578*rh*temp;
            if (rh < 51) return 2.22749 + 0.160107*rh - 0.01478*temp;
            return 21.06060 + 0.005565*rh*rh - 0.00035*rh*temp - 0.483199*rh;
        }
    }
}
