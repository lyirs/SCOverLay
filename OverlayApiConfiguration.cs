using System;
using System.IO;

namespace StarCitizenOverLay
{
    internal static class OverlayApiConfiguration
    {
        public static string? LoadApiBaseUrl()
        {
            var envFilePath = Path.Combine(AppContext.BaseDirectory, ".env");
            if (!File.Exists(envFilePath))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(envFilePath))
            {
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                var value = line[(separatorIndex + 1)..].Trim().Trim('"');

                if (!key.Equals("API_BASE_URL", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return Uri.TryCreate(value, UriKind.Absolute, out _) ? value : null;
            }

            return null;
        }
    }
}
