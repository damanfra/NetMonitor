using System;
using System.Globalization;
using NetMonitor.Localization;

namespace NetMonitor.Services
{
    public static class Format
    {
        private static CultureInfo Culture => Loc.Instance.Culture;
        private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

        public static string Bytes(long bytes)
        {
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < Units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.0" : "0", Culture) + " " + Units[unit];
        }

        public static string Rate(long bytesPerSecond) => bytesPerSecond <= 0 ? "—" : Bytes(bytesPerSecond) + "/s";

        public static string Ago(DateTime utc, DateTime nowUtc)
        {
            var d = nowUtc - utc;
            if (d.TotalSeconds < 5) return Loc.T("Ago_Now");
            if (d.TotalSeconds < 60) return Loc.T("Ago_Seconds", (int)d.TotalSeconds);
            if (d.TotalMinutes < 60) return Loc.T("Ago_Minutes", (int)d.TotalMinutes);
            return utc.ToLocalTime().ToString("t", Culture);
        }
    }
}
