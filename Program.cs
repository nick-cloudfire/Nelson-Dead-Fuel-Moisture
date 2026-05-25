using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PREACT.Fire;
using helpers;
using System.Diagnostics;
using System.Linq;

internal static class Program
{
    
    public static int Main(string[] args)
    {
        var Inv = CultureInfo.InvariantCulture;
        if (args.Length < 3)
        {
            Console.Error.WriteLine(
                "Usage: Program <weather.wxs> <demPath> <slopePath> <aspectPath> <canopyCoverPath> <preconditioningDays>"
            );
            return 2;
        }

        string wxsPath = args[0];
        string demPath = args.Length > 1 ? args[1] : "";
        string slopePath = args.Length > 2 ? args[2] : "";
        string aspectPath = args.Length > 3 ? args[3] : "";
        string canopyCoverPath = args.Length > 4 ? args[4] : "";
        long preconditioningHours = args.Length > 5 ? long.Parse(args[5], Inv) * 24 : 0;

        Console.WriteLine($"wxs:              {wxsPath}");
        Console.WriteLine($"dem:              {demPath}");
        Console.WriteLine($"slope:            {slopePath}");
        Console.WriteLine($"aspect:           {aspectPath}");
        Console.WriteLine($"canopy cover:     {canopyCoverPath}");
        Console.WriteLine($"preconditioning:  {args[5]} days ({preconditioningHours} hours)");

        var rows = WxsWeatherReader.ReadWxs(wxsPath);
        Console.WriteLine($"Weather rows:     {rows.Count}");
        var dem = BilReader.ReadSingleBandBsqAsInt2D(demPath, 0);
        var slp = BilReader.ReadSingleBandBsqAsInt2D(slopePath, 0);
        var asp = BilReader.ReadSingleBandBsqAsInt2D(aspectPath, 0);
        var cc = BilReader.ReadSingleBandBsqAsInt2D(canopyCoverPath, 0);
        var parentDir = Path.GetDirectoryName(Path.GetFullPath(demPath));
        Console.WriteLine($"Output dir:       {parentDir}");

        string hdrPath = System.IO.Path.ChangeExtension(demPath, ".hdr");
        var mi = BilReader.ReadMapInfo(hdrPath);

        var width = dem.GetLength(0);
        var length = dem.GetLength(1);
        var row = rows[0];
        int date = new DateTime(row.Year,row.Month,row.Day).DayOfYear;

        var rhFrac = row.RH / 100.0;
        var cumRainCm = row.HourlyPcpCm;
        double lat,lon = 0;

        uint[,] bins = new uint[width,length];

        Dictionary<uint, DeadFuelMoistureWrapper> deadfuel = new Dictionary<uint, DeadFuelMoistureWrapper>(8600);
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < length; y++)
            {
                if (cc[x,y] == mi.NoDataValue || asp[x,y] == mi.NoDataValue || slp[x,y] == mi.NoDataValue || dem[x,y] == mi.NoDataValue)
                {
                    bins[x,y]=0;
                }
                else
                {
                    bins[x,y] += (uint)(cc[x,y]/15);
                    bins[x,y] += (uint)(asp[x,y]/45) * 10;
                    bins[x,y] += (uint)(slp[x,y]/10) * 100;
                    bins[x,y] += (uint)(dem[x,y]/200) * 1000; 
                }
                if (!deadfuel.ContainsKey(bins[x, y]))
                {
                    deadfuel.Add(bins[x,y], new DeadFuelMoistureWrapper());
                }
            }
        }
        Console.WriteLine($"Grid size:        {width} x {length}  ({dem.Length} cells)");
        Console.WriteLine($"Unique bins:      {deadfuel.Count}");
        (lat, lon) = BilReader.PixelToLatLon(mi, (int)(width/2), (int)(length/2));
        Console.WriteLine($"Center lat/lon:   {lat:F4}, {lon:F4}");
        float[,] m1Raster = new float[width,length];
        float[,] m10Raster = new float[width,length];
        float[,] m100Raster = new float[width,length];
        float[] mc1 = new float[deadfuel.Count];
        float[] mc10 = new float[deadfuel.Count];
        float[] mc100 = new float[deadfuel.Count];
        var demCell = new int[deadfuel.Count];
        var slpCell = new int[deadfuel.Count];
        var aspCell = new int[deadfuel.Count];
        var ccCell  = new int[deadfuel.Count];

        foreach (uint idx in deadfuel.Keys)
        {
            deadfuel[idx].dem = (int)((idx / 1000) * 200) + 100;
            deadfuel[idx].slp = (int)((idx / 100) % 10) * 10 + 5;
            deadfuel[idx].asp = (int)((idx / 10) % 10) * 45 + 23;
            deadfuel[idx].cc  = (int)(idx % 10) * 15 + 8;
        }


        foreach (uint idx in deadfuel.Keys)
        {
            double solarWm2 = SunRadiation.SimpleRadiation(lat,lon,date,row.Hour,row.CloudCov,deadfuel[idx].dem,deadfuel[idx].slp,deadfuel[idx].asp,deadfuel[idx].cc);
            deadfuel[idx].initializeEnvironments(row.Year, row.Month, row.Day, row.Hour, row.Minute, 0, row.TempC, rhFrac, solarWm2, cumRainCm);
        }
        Console.WriteLine($"Running {rows.Count} hours (BSQ output starts after hour {preconditioningHours})...");
        int bandsWritten = 0;
        for (int thour = 0; thour < rows.Count; thour++)
        {
            Console.Write($"\rHour {thour}/{rows.Count - 1}  bands written: {bandsWritten}      ");

            row = rows[thour];
            date = new DateTime(row.Year,row.Month,row.Day).DayOfYear;
            rhFrac = row.RH / 100.0;
            cumRainCm += row.HourlyPcpCm;
            Parallel.ForEach(deadfuel, kvp =>
            {
                uint idx = kvp.Key;
                double solarWm2 = SunRadiation.SimpleRadiation(lat,lon,date,row.Hour,row.CloudCov,deadfuel[idx].dem,deadfuel[idx].slp,deadfuel[idx].asp,deadfuel[idx].cc);
                
                deadfuel[idx].Update(row.Year, row.Month, row.Day, row.Hour, row.Minute, 0, row.TempC, rhFrac, solarWm2, cumRainCm);

                if (thour > preconditioningHours)
                {
                    deadfuel[idx].GetMoisture();
                    //mc1[x] = (float)(4 * helpers.WxsWeatherReader.getEMC(row.RH, row.TempC) / 100.0 + m10) / 5f;
                }
            });
            if (thour > preconditioningHours)
            {
                for (int k = 0; k < width; k++)
                {
                    for (int m = 0; m < length; m++)
                    {
                        var index = bins[k,m];
                        m1Raster[k,m]=deadfuel[index].m1*100;
                        m10Raster[k,m]=deadfuel[index].m10*100;
                        m100Raster[k,m]=deadfuel[index].m100*100;
                    }
                }
                BilReader.BsqAppendBand(hdrPath,parentDir + "/m1.bsq",m1Raster);
                BilReader.BsqAppendBand(hdrPath,parentDir + "/m10.bsq",m10Raster);
                BilReader.BsqAppendBand(hdrPath,parentDir + "/m100.bsq",m100Raster);
                bandsWritten++;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"Done. Wrote {bandsWritten} band(s) to {parentDir}/m1.bsq, m10.bsq, m100.bsq");
        return 0;
    }

}

    