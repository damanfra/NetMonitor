using System;
using System.Globalization;

namespace NetMonitor.Services
{
    public static class Format
    {
        private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");
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
            if (d.TotalSeconds < 5) return "agora";
            if (d.TotalSeconds < 60) return $"há {(int)d.TotalSeconds} s";
            if (d.TotalMinutes < 60) return $"há {(int)d.TotalMinutes} min";
            return utc.ToLocalTime().ToString("HH:mm", Culture);
        }
    }
}
