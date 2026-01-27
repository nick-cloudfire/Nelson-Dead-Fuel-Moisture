using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Buffers.Binary;

namespace helpers
{
    public static class BilReader
    {        
        public sealed class MapInfo
        {
            public string ProjectionName = "";
            public double RefPixelX;      // 1-based
            public double RefPixelY;      // 1-based
            public double RefEasting;     // map X at reference pixel
            public double RefNorthing;    // map Y at reference pixel
            public double PixelSizeX;
            public double PixelSizeY;
            public int? UtmZone;          // if projection is UTM
            public bool? IsNorthHemisphere;
            public string Datum = "";     // often "WGS-84"
            public string Units = "";
        }

        /// <summary>
        /// Reads ENVI "map info" from a .hdr file.
        /// Requires a line like: map info = {UTM, 1, 1, 500000, 4100000, 30, 30, 17, North, WGS-84, units=Meters}
        /// </summary>
        public static MapInfo ReadMapInfo(string hdrPath)
        {
            if (!File.Exists(hdrPath))
                throw new FileNotFoundException("ENVI .hdr file not found.", hdrPath);

            string? mapInfoRaw = null;

            foreach (var rawLine in File.ReadLines(hdrPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("map info", StringComparison.OrdinalIgnoreCase))
                {
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                    {
                        mapInfoRaw = line.Substring(eq + 1).Trim();
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(mapInfoRaw))
                throw new InvalidDataException("ENVI header missing 'map info'.");

            // map info = { ... }
            mapInfoRaw = mapInfoRaw.Trim();
            mapInfoRaw = mapInfoRaw.TrimStart('{').TrimEnd('}');

            // Split by commas, but ENVI is simple enough that commas are safe here.
            // (If you have quoted commas inside strings, you’ll need a CSV/quoted parser.)
            var parts = SplitCsvLoose(mapInfoRaw);

            if (parts.Count < 6)
                throw new InvalidDataException("Unexpected ENVI 'map info' format.");

            double D(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

            var mi = new MapInfo
            {
                ProjectionName = parts[0].Trim(),
                RefPixelX = D(parts[1]),
                RefPixelY = D(parts[2]),
                RefEasting = D(parts[3]),
                RefNorthing = D(parts[4]),
                PixelSizeX = D(parts[5]),
                PixelSizeY = (parts.Count > 6) ? D(parts[6]) : D(parts[5]) // sometimes only one is provided
            };

            // Try to pull UTM zone + hemisphere if present
            // Typical ENVI UTM: [0]=UTM, [7]=zone, [8]=North/South
            for (int i = 7; i < parts.Count; i++)
            {
                var p = parts[i].Trim();

                if (mi.UtmZone == null && int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
                    mi.UtmZone = z;

                if (mi.IsNorthHemisphere == null)
                {
                    if (p.Equals("North", StringComparison.OrdinalIgnoreCase)) mi.IsNorthHemisphere = true;
                    if (p.Equals("South", StringComparison.OrdinalIgnoreCase)) mi.IsNorthHemisphere = false;
                }

                // Datum often appears as WGS-84
                if (string.IsNullOrEmpty(mi.Datum) &&
                    (p.Contains("WGS", StringComparison.OrdinalIgnoreCase) || p.Contains("NAD", StringComparison.OrdinalIgnoreCase)))
                {
                    mi.Datum = p;
                }

                // units=...
                if (p.StartsWith("units", StringComparison.OrdinalIgnoreCase))
                    mi.Units = p;
            }

            return mi;
        }

        /// <summary>
        /// Convert pixel coordinates (0-based x,y) to projected coordinates (Easting, Northing),
        /// using ENVI "map info".
        /// </summary>
        public static (double Easting, double Northing) PixelToProjected(MapInfo mi, int x, int y)
        {
            // ENVI reference pixel is 1-based; convert to 0-based for math against x,y (0-based)
            double refX0 = mi.RefPixelX - 1.0;
            double refY0 = mi.RefPixelY - 1.0;

            // Easting increases with x to the right
            double e = mi.RefEasting + (x - refX0) * mi.PixelSizeX;

            // Northing usually decreases as y increases (top-left origin), so subtract
            double n = mi.RefNorthing - (y - refY0) * mi.PixelSizeY;

            return (e, n);
        }

        /// <summary>
        /// Convert pixel (0-based x,y) directly to (lat, lon) in degrees.
        /// Supports:
        /// - UTM (WGS84)  -> uses mi.UtmZone and mi.IsNorthHemisphere
        /// - Geographic Lat/Lon -> assumes projected coords are (lon, lat)
        /// </summary>
        public static (double Lat, double Lon) PixelToLatLon(MapInfo mi, int x, int y)
        {
            var (e, n) = PixelToProjected(mi, x, y);

            // Geographic Lat/Lon (ENVI often uses "Geographic Lat/Lon" or similar)
            if (mi.ProjectionName.Contains("Geographic", StringComparison.OrdinalIgnoreCase) ||
                mi.ProjectionName.Contains("Lat", StringComparison.OrdinalIgnoreCase) ||
                mi.ProjectionName.Contains("Lon", StringComparison.OrdinalIgnoreCase))
            {
                // ENVI often stores X=longitude, Y=latitude
                return (Lat: n, Lon: e);
            }

            // UTM
            if (mi.ProjectionName.Equals("UTM", StringComparison.OrdinalIgnoreCase) ||
                mi.ProjectionName.Contains("UTM", StringComparison.OrdinalIgnoreCase))
            {
                if (mi.UtmZone == null || mi.IsNorthHemisphere == null)
                    throw new InvalidDataException("UTM 'map info' missing zone and/or hemisphere.");

                // This implementation assumes WGS84 ellipsoid (most common). If your datum is different, you need the right ellipsoid.
                return UtmToLatLon_Wgs84(e, n, mi.UtmZone.Value, mi.IsNorthHemisphere.Value);
            }

            throw new NotSupportedException($"Projection not supported without a projection library: '{mi.ProjectionName}'.");
        }

        /// <summary>
        /// UTM -> lat/lon (WGS84) using standard inverse formulas.
        /// Returns degrees.
        /// </summary>
        public static (double Lat, double Lon) UtmToLatLon_Wgs84(double easting, double northing, int zone, bool isNorthHemisphere)
        {
            // WGS84
            const double a = 6378137.0;                  // semi-major axis
            const double f = 1.0 / 298.257223563;        // flattening
            const double k0 = 0.9996;

            double e2 = f * (2 - f);                     // eccentricity^2
            double ep2 = e2 / (1 - e2);                  // second eccentricity^2

            // Remove false easting
            double x = easting - 500000.0;

            // Remove false northing for southern hemisphere
            double y = northing;
            if (!isNorthHemisphere)
                y -= 10000000.0;

            // Meridian arc
            double M = y / k0;

            // Footpoint latitude
            double mu = M / (a * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));

            double e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));

            double J1 = (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32);
            double J2 = (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32);
            double J3 = (151 * Math.Pow(e1, 3) / 96);
            double J4 = (1097 * Math.Pow(e1, 4) / 512);

            double fp = mu
                        + J1 * Math.Sin(2 * mu)
                        + J2 * Math.Sin(4 * mu)
                        + J3 * Math.Sin(6 * mu)
                        + J4 * Math.Sin(8 * mu);

            double sinfp = Math.Sin(fp);
            double cosfp = Math.Cos(fp);
            double tanfp = Math.Tan(fp);

            double C1 = ep2 * cosfp * cosfp;
            double T1 = tanfp * tanfp;
            double N1 = a / Math.Sqrt(1 - e2 * sinfp * sinfp);
            double R1 = a * (1 - e2) / Math.Pow(1 - e2 * sinfp * sinfp, 1.5);
            double D = x / (N1 * k0);

            // Latitude (radians)
            double lat =
                fp - (N1 * tanfp / R1) *
                (D * D / 2
                 - (5 + 3 * T1 + 10 * C1 - 4 * C1 * C1 - 9 * ep2) * Math.Pow(D, 4) / 24
                 + (61 + 90 * T1 + 298 * C1 + 45 * T1 * T1 - 252 * ep2 - 3 * C1 * C1) * Math.Pow(D, 6) / 720);

            // Central meridian of zone
            double lon0 = DegToRad(-183.0 + 6.0 * zone);

            // Longitude (radians)
            double lon =
                lon0 + (D
                        - (1 + 2 * T1 + C1) * Math.Pow(D, 3) / 6
                        + (5 - 2 * C1 + 28 * T1 - 3 * C1 * C1 + 8 * ep2 + 24 * T1 * T1) * Math.Pow(D, 5) / 120) / cosfp;

            return (Lat: RadToDeg(lat), Lon: RadToDeg(lon));
        }

        private static double DegToRad(double deg) => deg * (Math.PI / 180.0);
        private static double RadToDeg(double rad) => rad * (180.0 / Math.PI);

        private static List<string> SplitCsvLoose(string s)
        {
            var parts = new List<string>();
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == ',')
                {
                    parts.Add(s.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(s.Substring(start).Trim());
            return parts;
        }
        public sealed class EnviHdr
        {
            public int Samples;
            public int Lines;
            public int Bands;
            public int DataType;
            public int ByteOrder;
            public long HeaderOffset;
            public string Interleave = "bsq"; // default
        }


        /// <summary>
        /// Reads a single-band ENVI BIL raster into a float[] (row-major).
        /// Returns: (data, width, height)
        /// </summary>
        /// <summary>
/// Reads a single band (0-based bandIndex) from an ENVI BSQ raster into int[,] (lines x samples).
/// </summary>
        public static int[,] ReadSingleBandBsqAsInt2D(
            string bsqPath,
            int bandIndex,
            string? hdrPath = null)
        {
            if (!File.Exists(bsqPath))
                throw new FileNotFoundException("Raster file not found.", bsqPath);

            hdrPath ??= Path.ChangeExtension(bsqPath, ".hdr");
            if (!File.Exists(hdrPath))
                throw new FileNotFoundException("ENVI .hdr file not found.", hdrPath);

            var hdr = ParseEnviHdr(hdrPath); // now enforces BSQ

            if (bandIndex < 0 || bandIndex >= hdr.Bands)
                throw new ArgumentOutOfRangeException(nameof(bandIndex));

            int width = hdr.Samples;
            int height = hdr.Lines;
            int bps = BytesPerSample(hdr.DataType);

            long bandBytes = (long)width * height * bps;
            long bandOffset = hdr.HeaderOffset + bandBytes * bandIndex;

            var data = new int[height, width];

            using var fs = new FileStream(bsqPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = bandOffset;

            byte[] rowBuf = new byte[width * bps];

            for (int y = 0; y < height; y++)
            {
                ReadExactly(fs, rowBuf, 0, rowBuf.Length);

                for (int x = 0; x < width; x++)
                {
                    int o = x * bps;
                    data[y, x] = ReadSampleAsInt(rowBuf, o, hdr.DataType, hdr.ByteOrder);
                }
            }

            return data;
        }



        /// <summary>Parse a minimal ENVI header needed for BIL reading.</summary>
        public static EnviHdr ParseEnviHdr(string hdrPath)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in File.ReadLines(hdrPath))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (line.StartsWith("ENVI", StringComparison.OrdinalIgnoreCase)) continue;

                int semi = line.IndexOf(';');
                if (semi >= 0) line = line.Substring(0, semi).Trim();
                if (string.IsNullOrEmpty(line)) continue;

                int eq = line.IndexOf('=');
                if (eq < 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                map[key] = value.Trim();
            }

            int GetInt(string k, int defaultValue = 0)
            {
                if (!map.TryGetValue(k, out var v)) return defaultValue;
                v = v.Trim().Trim('{', '}').Trim();
                return int.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }

            long GetLong(string k, long defaultValue = 0)
            {
                if (!map.TryGetValue(k, out var v)) return defaultValue;
                v = v.Trim().Trim('{', '}').Trim();
                return long.Parse(v, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }

            string GetString(string k, string defaultValue = "")
            {
                if (!map.TryGetValue(k, out var v)) return defaultValue;
                v = v.Trim().Trim('{', '}').Trim();
                return v;
            }

            var hdr = new EnviHdr
            {
                Samples = GetInt("samples"),
                Lines = GetInt("lines"),
                Bands = GetInt("bands", 1),
                DataType = GetInt("data type"),
                ByteOrder = GetInt("byte order", 0),
                HeaderOffset = GetLong("header offset", 0),
                Interleave = GetString("interleave", "bsq").ToLowerInvariant()
            };

            if (hdr.DataType == 0)
                throw new InvalidDataException("ENVI header missing or invalid: 'data type'.");

            if (hdr.ByteOrder != 0 && hdr.ByteOrder != 1)
                throw new InvalidDataException($"Unexpected 'byte order' value: {hdr.ByteOrder}");

            if (hdr.Interleave != "bsq")
                throw new NotSupportedException($"This reader expects BSQ. Found interleave='{hdr.Interleave}'.");

            return hdr;
        }


        private static int BytesPerSample(int enviDataType) => enviDataType switch
        {
            1  => 1, // byte
            2  => 2, // int16
            3  => 4, // int32
            4  => 4, // float32
            5  => 8, // float64
            12 => 2, // uint16
            13 => 4, // uint32
            14 => 8, // int64
            15 => 8, // uint64
            _ => throw new NotSupportedException($"Unsupported ENVI data type: {enviDataType}")
        };

        private static int ReadSampleAsInt(byte[] buf, int offset, int enviDataType, int byteOrder)
        {
            bool wantLittle = (byteOrder == 0);
            bool needSwap = (BitConverter.IsLittleEndian != wantLittle);

            static int ClampToInt(double v)
            {
                if (v > int.MaxValue) return int.MaxValue;
                if (v < int.MinValue) return int.MinValue;
                return (int)Math.Round(v);
            }

            switch (enviDataType)
            {
                case 1: // byte
                    return buf[offset];

                case 2: // int16
                    return needSwap
                        ? BitConverter.ToInt16(ReverseCopy(buf, offset, 2), 0)
                        : BitConverter.ToInt16(buf, offset);

                case 12: // uint16
                    return needSwap
                        ? BitConverter.ToUInt16(ReverseCopy(buf, offset, 2), 0)
                        : BitConverter.ToUInt16(buf, offset);

                case 3: // int32
                    return needSwap
                        ? BitConverter.ToInt32(ReverseCopy(buf, offset, 4), 0)
                        : BitConverter.ToInt32(buf, offset);

                case 13: // uint32
                {
                    uint v = needSwap
                        ? BitConverter.ToUInt32(ReverseCopy(buf, offset, 4), 0)
                        : BitConverter.ToUInt32(buf, offset);
                    return v > int.MaxValue ? int.MaxValue : (int)v;
                }

                case 4: // float32
                {
                    float v = needSwap
                        ? BitConverter.ToSingle(ReverseCopy(buf, offset, 4), 0)
                        : BitConverter.ToSingle(buf, offset);
                    return ClampToInt((double)v);
                }

                case 5: // float64
                {
                    double v = needSwap
                        ? BitConverter.ToDouble(ReverseCopy(buf, offset, 8), 0)
                        : BitConverter.ToDouble(buf, offset);
                    return ClampToInt(v);
                }

                case 14: // int64
                {
                    long v = needSwap
                        ? BitConverter.ToInt64(ReverseCopy(buf, offset, 8), 0)
                        : BitConverter.ToInt64(buf, offset);
                    return ClampToInt(v);
                }

                case 15: // uint64
                {
                    ulong v = needSwap
                        ? BitConverter.ToUInt64(ReverseCopy(buf, offset, 8), 0)
                        : BitConverter.ToUInt64(buf, offset);
                    return v > int.MaxValue ? int.MaxValue : (int)v;
                }

                default:
                    throw new NotSupportedException($"Unsupported ENVI data type: {enviDataType}");
            }
        }


        private static byte[] ReverseCopy(byte[] src, int offset, int count)
        {
            var tmp = new byte[count];
            Buffer.BlockCopy(src, offset, tmp, 0, count);
            Array.Reverse(tmp);
            return tmp;
        }

        private static void ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = s.Read(buffer, offset + total, count - total);
                if (n <= 0) throw new EndOfStreamException("Unexpected EOF while reading raster.");
                total += n;
            }
        }

        public static void BsqAppendBand(
            string sampleHdrPath,
            string outRasterPath,
            float[,] band,
            int byteOrder = 0) // 0 little, 1 big
        {
            if (!File.Exists(sampleHdrPath))
                throw new FileNotFoundException("Sample ENVI .hdr not found.", sampleHdrPath);

            string outHdrPath = Path.ChangeExtension(outRasterPath, ".hdr");

            int lines = band.GetLength(0);
            int samples = band.GetLength(1);

            // If output doesn't exist, create header + empty file
            if (!File.Exists(outRasterPath))
            {
                // Create new HDR with bands=0 initially (we'll set to 1 after first append)
                var hdrText = BuildOutputHdrFromSample(sampleHdrPath, samples, lines, bands: 0, byteOrder);
                File.WriteAllText(outHdrPath, hdrText);

                using (var fs = new FileStream(outRasterPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // no header offset
                }
            }

            // Validate output header dimensions
            var outHdr = BilReader.ParseEnviHdr(outHdrPath);
            if (outHdr.Interleave != "bsq")
                throw new InvalidDataException($"Output interleave must be bsq, found '{outHdr.Interleave}'.");

            if (outHdr.Samples != samples || outHdr.Lines != lines)
                throw new InvalidDataException("Band dimensions do not match output raster header.");

            // Append band bytes at EOF
            WriteBandAtEof(outRasterPath, band, byteOrder);

            // Increment bands in HDR
            UpdateHdrBands(outHdrPath, outHdr.Bands + 1);
        }

        private static void WriteBandAtEof(string outRasterPath, float[,] band, int byteOrder)
        {
            int lines = band.GetLength(0);
            int samples = band.GetLength(1);

            bool little = (byteOrder == 0);

            // Bigger buffer reduces syscalls if you later switch to buffered batching
            using var fs = new FileStream(outRasterPath, FileMode.Open, FileAccess.Write, FileShare.None, bufferSize: 1 << 20);
            fs.Seek(0, SeekOrigin.End);

            byte[] row = new byte[samples * 4];

            for (int y = 0; y < lines; y++)
            {
                var span = row.AsSpan();
                int o = 0;

                for (int x = 0; x < samples; x++)
                {
                    float v = band[y, x];
                    if (little)
                        BinaryPrimitives.WriteSingleLittleEndian(span.Slice(o, 4), v);
                    else
                        BinaryPrimitives.WriteSingleBigEndian(span.Slice(o, 4), v);
                    o += 4;
                }

                fs.Write(row, 0, row.Length);
            }
        }


        private static string BuildOutputHdrFromSample(
            string sampleHdrPath,
            int samples,
            int lines,
            int bands,
            int byteOrder)
        {
            string sample = File.ReadAllText(sampleHdrPath);

            var sb = new StringBuilder();
            sb.AppendLine("ENVI");
            sb.AppendLine($"samples = {samples}");
            sb.AppendLine($"lines = {lines}");
            sb.AppendLine($"bands = {bands}");
            sb.AppendLine("header offset = 0");
            sb.AppendLine("file type = ENVI Standard");
            sb.AppendLine("data type = 4"); // float32
            sb.AppendLine("interleave = bsq");
            sb.AppendLine($"byte order = {byteOrder}");

            // Copy georeferencing-ish fields if present (single-line or brace blocks on one line)
            CopyHdrLineIfPresent(sample, sb, "map info");
            CopyHdrLineIfPresent(sample, sb, "projection info");
            CopyHdrLineIfPresent(sample, sb, "coordinate system string");

            // Optional fields you might want to preserve
            CopyHdrLineIfPresent(sample, sb, "data ignore value");
            CopyHdrLineIfPresent(sample, sb, "x start");
            CopyHdrLineIfPresent(sample, sb, "y start");

            return sb.ToString();
        }

        private static void CopyHdrLineIfPresent(string hdrText, StringBuilder sb, string key)
        {
            // Grabs a single line like "map info = { ... }"
            var re = new Regex($@"(?im)^\s*{Regex.Escape(key)}\s*=\s*.+$");
            var m = re.Match(hdrText);
            if (m.Success)
                sb.AppendLine(m.Value.Trim());
        }

        private static void UpdateHdrBands(string hdrPath, int newBands)
        {
            string txt = File.ReadAllText(hdrPath);

            var re = new Regex(@"(?im)^\s*bands\s*=\s*\d+\s*$");
            if (re.IsMatch(txt))
                txt = re.Replace(txt, $"bands = {newBands}", 1);
            else
                txt = "ENVI\n" + $"bands = {newBands}\n" + txt;

            File.WriteAllText(hdrPath, txt);
        }
    }
}
