using System.Globalization;

namespace Valve.Core;

/// <summary>
/// Unit converter supporting bit and byte bandwidth scales (Item 1.11).
/// Supports B/s, KB/s, MB/s, GB/s, bps, Kbps, Mbps, Gbps.
/// </summary>
public static class UnitConverter
{
    public static readonly string[] UnitOptions = { "B/s", "KB/s", "MB/s", "GB/s", "bps", "Kbps", "Mbps", "Gbps" };

    public static long ParseToBps(double value, string unit)
    {
        if (value <= 0) return 0;
        return unit.Trim() switch
        {
            "B/s" => (long)Math.Round(value),
            "KB/s" => (long)Math.Round(value * 1024.0),
            "MB/s" => (long)Math.Round(value * 1024.0 * 1024.0),
            "GB/s" => (long)Math.Round(value * 1024.0 * 1024.0 * 1024.0),
            "bps" => (long)Math.Round(value / 8.0),
            "Kbps" or "kbit/s" => (long)Math.Round(value * 1000.0 / 8.0),
            "Mbps" or "Mbit/s" => (long)Math.Round(value * 1_000_000.0 / 8.0),
            "Gbps" or "Gbit/s" => (long)Math.Round(value * 1_000_000_000.0 / 8.0),
            _ => (long)Math.Round(value * 1024.0),
        };
    }

    public static int ParseToKbps(double value, string unit)
    {
        long bps = ParseToBps(value, unit);
        if (bps <= 0) return 0;
        return (int)Math.Max(1, Math.Round(bps * 8.0 / 1024.0));
    }

    public static (double Value, string Unit) FormatOptimal(long bytesPerSec, bool useBits)
    {
        if (bytesPerSec <= 0)
        {
            return (0, useBits ? "Kbps" : "KB/s");
        }

        if (useBits)
        {
            double bits = bytesPerSec * 8.0;
            if (bits >= 1_000_000_000.0)
            {
                return (Math.Round(bits / 1_000_000_000.0, 2), "Gbps");
            }
            if (bits >= 1_000_000.0)
            {
                return (Math.Round(bits / 1_000_000.0, 2), "Mbps");
            }
            return (Math.Round(bits / 1000.0, 1), "Kbps");
        }
        else
        {
            double bytes = bytesPerSec;
            if (bytes >= 1024.0 * 1024.0 * 1024.0)
            {
                return (Math.Round(bytes / (1024.0 * 1024.0 * 1024.0), 2), "GB/s");
            }
            if (bytes >= 1024.0 * 1024.0)
            {
                return (Math.Round(bytes / (1024.0 * 1024.0), 2), "MB/s");
            }
            return (Math.Round(bytes / 1024.0, 1), "KB/s");
        }
    }

    public static double Convert(double value, string fromUnit, string toUnit)
    {
        long bps = ParseToBps(value, fromUnit);
        return toUnit.Trim() switch
        {
            "B/s" => bps,
            "KB/s" => (double)bps / 1024.0,
            "MB/s" => (double)bps / (1024.0 * 1024.0),
            "GB/s" => (double)bps / (1024.0 * 1024.0 * 1024.0),
            "bps" => bps * 8.0,
            "Kbps" or "kbit/s" => (bps * 8.0) / 1000.0,
            "Mbps" or "Mbit/s" => (bps * 8.0) / 1_000_000.0,
            "Gbps" or "Gbit/s" => (bps * 8.0) / 1_000_000_000.0,
            _ => (double)bps / 1024.0,
        };
    }

    public static string FormatString(long bytesPerSec, bool useBits)
    {
        var (val, unit) = FormatOptimal(bytesPerSec, useBits);
        return $"{val.ToString("0.##", CultureInfo.InvariantCulture)} {unit}";
    }
}
