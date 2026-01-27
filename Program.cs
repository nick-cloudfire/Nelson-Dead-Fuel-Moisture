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

        var rows = WxsWeatherReader.ReadWxs(wxsPath);
        var dem = BilReader.ReadSingleBandBsqAsInt2D(demPath, 0);
        var slp = BilReader.ReadSingleBandBsqAsInt2D(slopePath, 0);
        var asp = BilReader.ReadSingleBandBsqAsInt2D(aspectPath, 0);
        var cc = BilReader.ReadSingleBandBsqAsInt2D(canopyCoverPath, 0);
        var parentDir = Path.GetDirectoryName(demPath);

        string hdrPath = System.IO.Path.ChangeExtension(demPath, ".hdr");
        var mi = BilReader.ReadMapInfo(hdrPath);

        var width = dem.GetLength(0);
        var length = dem.GetLength(1);
        var row = rows[0];
        int date = new DateTime(row.Year,row.Month,row.Day).DayOfYear;

        var rhFrac = row.RH / 100.0;
        var cumRainCm = row.HourlyPcpCm;
        double lat,lon = 0;

        int[,] bins = new int[width,length];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < length; y++)
            {
                bins[x,y] += (int)(cc[x,y]/15);
                bins[x,y] += (int)(asp[x,y]/45) * 10;
                bins[x,y] += (int)(slp[x,y]/10) * 100;
                bins[x,y] += (int)(dem[x,y]/200) * 1000; 
            }
        }
        int max = bins.Cast<int>().Max();
        int[] unique = new int [max+1];
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < length; y++)
            {
                unique[bins[x,y]]++; 
            }
        }

        DeadFuelMoisture[] dfm1 = new DeadFuelMoisture[max + 1];
        DeadFuelMoisture[] dfm10 = new DeadFuelMoisture[max + 1];
        DeadFuelMoisture[] dfm100 = new DeadFuelMoisture[max + 1];
        (lat, lon) = BilReader.PixelToLatLon(mi, (int)(width/2), (int)(length/2));
        float[,] m1Raster = new float[width,length];
        float[,] m10Raster = new float[width,length];
        float[,] m100Raster = new float[width,length];
        float[] mc1 = new float[max + 1];
        float[] mc10 = new float[max + 1];
        float[] mc100 = new float[max + 1];
        var demCell = new int[max + 1];
        var slpCell = new int[max + 1];
        var aspCell = new int[max + 1];
        var ccCell  = new int[max + 1];

        for (int idx = 0; idx < max + 1; idx++)
        {
            demCell[idx] = ((idx / 1000) * 200) + 100;
            slpCell[idx] = ((idx / 100) % 10) * 10 + 5;
            aspCell[idx] = ((idx / 10) % 10) * 45 + 23;
            ccCell[idx]  = (idx % 10) * 15 + 8;
        }


        for (int i = 0; i < max + 1; i++)
        {
            if (unique[i] <=0) continue;
            dfm1[i]=DeadFuelMoisture.createDeadFuelMoisture1("1h");
            dfm1[i].initializeStick();
            dfm10[i]=DeadFuelMoisture.createDeadFuelMoisture10("10h");
            dfm10[i].initializeStick();
            dfm100[i]=DeadFuelMoisture.createDeadFuelMoisture100("100h");
            dfm100[i].initializeStick();

            double solarWm2 = SunRadiation.SimpleRadiation(lat,lon,date,row.Hour,row.CloudCov,demCell[i],slpCell[i],aspCell[i],ccCell[i]);

            dfm1[i].initializeEnvironment(
                row.Year, row.Month, row.Day, row.Hour, row.Minute, 0,
                row.TempC, rhFrac, solarWm2, cumRainCm,
                row.TempC, rhFrac, 0.08
            );

            dfm10[i].initializeEnvironment(
                row.Year, row.Month, row.Day, row.Hour, row.Minute, 0,
                row.TempC, rhFrac, solarWm2, cumRainCm,
                row.TempC, rhFrac, 0.10
            );

            dfm100[i].initializeEnvironment(
                row.Year, row.Month, row.Day, row.Hour, row.Minute, 0,
                row.TempC, rhFrac, solarWm2, cumRainCm,
                row.TempC, rhFrac, 0.12
            );
        }
        for (int thour = 1; thour < rows.Count; thour++)
        {
            Console.Write($"\rStarting Hour {thour}      ");

            row = rows[thour];
            date = new DateTime(row.Year,row.Month,row.Day).DayOfYear;
            rhFrac = row.RH / 100.0;
            cumRainCm += row.HourlyPcpCm;
            
            Parallel.For(0, unique.Length, x =>
            {
                if (unique[x] <=0) return;
                double solarWm2 = SunRadiation.SimpleRadiation(lat,lon,date,row.Hour,row.CloudCov,demCell[x],slpCell[x],aspCell[x],ccCell[x]);
                
                dfm1[x].update(row.Year, row.Month, row.Day, row.Hour, row.Minute, 0, row.TempC, rhFrac, solarWm2, cumRainCm);
                dfm10[x].update(row.Year, row.Month, row.Day, row.Hour, row.Minute, 0, row.TempC, rhFrac, solarWm2, cumRainCm);
                dfm100[x].update(row.Year, row.Month, row.Day, row.Hour, row.Minute, 0, row.TempC, rhFrac, solarWm2, cumRainCm);

                if (thour - preconditioningHours > 0)
                {
                    float m1 = dfm1[x].meanWtdMoisture();
                    float m10 = dfm10[x].meanWtdMoisture();
                    float m100 = dfm100[x].meanWtdMoisture();
                    mc1[x] = m1;
                    mc10[x] = m10;
                    mc100[x] = m100;
                    //mc1[x] = (float)(4 * helpers.WxsWeatherReader.getEMC(row.RH, row.TempC) / 100.0 + m10) / 5f;
                }
            });
            if (thour - preconditioningHours > 0)
            {
                for (int k = 0; k < width; k++)
                {
                    for (int m = 0; m < length; m++)
                    {
                        var index = bins[k,m];
                        m1Raster[k,m]=mc1[index]*100;
                        m10Raster[k,m]=mc10[index]*100;
                        m100Raster[k,m]=mc100[index]*100;
                    }
                }
                BilReader.BsqAppendBand(hdrPath,parentDir + "/m1.bsq",m1Raster);
                BilReader.BsqAppendBand(hdrPath,parentDir + "/m10.bsq",m10Raster);
                BilReader.BsqAppendBand(hdrPath,parentDir + "/m100.bsq",m100Raster);
            }     
        }
        Console.WriteLine();
        return 0;
    }

}

    