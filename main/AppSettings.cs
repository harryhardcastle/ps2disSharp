using System;
using System.IO;
using System.Text;

namespace PS2Disassembler
{
    internal sealed class AppSettings
    {
        // Application version — increment by 0.0.001 whenever packaging the source into a zip
        public const string AppVersion = "0.0.068";

        // Defaults
        public const string DefaultFontFamily = "Liberation Mono";
        public const float DefaultFontSize = 9f;
        public const string DefaultTheme = "Dark";
        public const int DefaultRefreshRate = 30;
        public const int DefaultConstantWriteRate = 1;
        public const string DefaultDebugHost = "127.0.0.1";
        public const int DefaultPinePort = 28011;
        public const int DefaultMcpPort = 21512;
        public const bool DefaultShowMemoryView = true;
        public static readonly int[] SupportedRefreshRates = { 1, 10, 20, 30, 60, 100 };
        public static readonly int[] SupportedConstantWriteRates = { 1, 10, 20, 30, 60 };

        public string FontFamily { get; set; } = DefaultFontFamily;
        public float FontSize { get; set; } = DefaultFontSize;
        public string Theme { get; set; } = DefaultTheme;
        public int RefreshRate { get; set; } = DefaultRefreshRate;
        public int ConstantWriteRate { get; set; } = DefaultConstantWriteRate;
        public string DebugHost { get; set; } = DefaultDebugHost;
        public int PinePort { get; set; } = DefaultPinePort;
        public int McpPort { get; set; } = DefaultMcpPort;
        public bool ShowMemoryView { get; set; } = DefaultShowMemoryView;

        private static string ConfigPath =>
            Path.Combine(AppContext.BaseDirectory, "ps2dis_settings.json");

        public static AppSettings Load()
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path))
                {
                    var defaults = new AppSettings();
                    defaults.Save();
                    return defaults;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                return ParseJson(json);
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            try
            {
                string json = ToJson();
                File.WriteAllText(ConfigPath, json, Encoding.UTF8);
            }
            catch
            {
                // non-fatal
            }
        }

        public void ResetToDefaults()
        {
            FontFamily = DefaultFontFamily;
            FontSize = DefaultFontSize;
            Theme = DefaultTheme;
            RefreshRate = DefaultRefreshRate;
            ConstantWriteRate = DefaultConstantWriteRate;
            DebugHost = DefaultDebugHost;
            PinePort = DefaultPinePort;
            McpPort = DefaultMcpPort;
            ShowMemoryView = DefaultShowMemoryView;
        }

        private string ToJson()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"FontFamily\": \"{EscapeJsonString(FontFamily)}\",");
            sb.AppendLine($"  \"FontSize\": {FontSize:F1},");
            sb.AppendLine($"  \"Theme\": \"{EscapeJsonString(Theme)}\",");
            sb.AppendLine($"  \"RefreshRate\": {RefreshRate},");
            sb.AppendLine($"  \"ConstantWriteRate\": {ConstantWriteRate},");
            sb.AppendLine($"  \"DebugHost\": \"{EscapeJsonString(NormalizeDebugHost(DebugHost))}\",");
            sb.AppendLine($"  \"PinePort\": {NormalizePort(PinePort, DefaultPinePort)},");
            sb.AppendLine($"  \"McpPort\": {NormalizePort(McpPort, DefaultMcpPort)},");
            sb.AppendLine($"  \"ShowMemoryView\": {(ShowMemoryView ? "true" : "false")}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string EscapeJsonString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static AppSettings ParseJson(string json)
        {
            var settings = new AppSettings();

            string? fontFamily = ExtractStringValue(json, "FontFamily");
            if (fontFamily != null) settings.FontFamily = fontFamily;

            float? fontSize = ExtractFloatValue(json, "FontSize");
            if (fontSize.HasValue && fontSize.Value >= 6f && fontSize.Value <= 30f)
                settings.FontSize = fontSize.Value;

            string? theme = ExtractStringValue(json, "Theme");
            if (theme != null) settings.Theme = theme;

            int? refreshRate = ExtractIntValue(json, "RefreshRate");
            if (refreshRate.HasValue && IsSupportedRefreshRate(refreshRate.Value))
                settings.RefreshRate = refreshRate.Value;

            int? constantWriteRate = ExtractIntValue(json, "ConstantWriteRate");
            if (constantWriteRate.HasValue && IsSupportedConstantWriteRate(constantWriteRate.Value))
                settings.ConstantWriteRate = constantWriteRate.Value;

            string? debugHost = ExtractStringValue(json, "DebugHost");
            settings.DebugHost = NormalizeDebugHost(debugHost);

            int? pinePort = ExtractIntValue(json, "PinePort");
            settings.PinePort = NormalizePort(pinePort, DefaultPinePort);

            int? mcpPort = ExtractIntValue(json, "McpPort");
            settings.McpPort = NormalizePort(mcpPort, DefaultMcpPort);

            bool? showMemoryView = ExtractBoolValue(json, "ShowMemoryView");
            settings.ShowMemoryView = showMemoryView ?? DefaultShowMemoryView;

            return settings;
        }

        private static string? ExtractStringValue(string json, string key)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int quoteStart = json.IndexOf('"', colonIdx + 1);
            if (quoteStart < 0) return null;

            int quoteEnd = json.IndexOf('"', quoteStart + 1);
            if (quoteEnd < 0) return null;

            return json.Substring(quoteStart + 1, quoteEnd - quoteStart - 1)
                       .Replace("\\\\", "\\").Replace("\\\"", "\"");
        }

        public static bool IsSupportedRefreshRate(int refreshRate)
        {
            for (int i = 0; i < SupportedRefreshRates.Length; i++)
            {
                if (SupportedRefreshRates[i] == refreshRate)
                    return true;
            }
            return false;
        }

        public static bool IsSupportedConstantWriteRate(int rate)
        {
            for (int i = 0; i < SupportedConstantWriteRates.Length; i++)
            {
                if (SupportedConstantWriteRates[i] == rate)
                    return true;
            }
            return false;
        }

        public static string NormalizeDebugHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return DefaultDebugHost;

            string trimmed = host.Trim();
            return trimmed.Length == 0 ? DefaultDebugHost : trimmed;
        }

        public static int NormalizePort(int? port, int fallback)
        {
            return port.HasValue && port.Value >= 1 && port.Value <= 65535 ? port.Value : fallback;
        }

        private static bool? ExtractBoolValue(string json, string key)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int start = colonIdx + 1;
            while (start < json.Length && char.IsWhiteSpace(json[start]))
                start++;

            if (start + 4 <= json.Length && string.Compare(json, start, "true", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
                return true;
            if (start + 5 <= json.Length && string.Compare(json, start, "false", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
                return false;

            return null;
        }

        private static int? ExtractIntValue(string json, string key)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int start = colonIdx + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t'))
                start++;

            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-'))
                end++;

            if (end > start && int.TryParse(json.Substring(start, end - start),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int val))
                return val;

            return null;
        }

        private static float? ExtractFloatValue(string json, string key)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            int colonIdx = json.IndexOf(':', idx + pattern.Length);
            if (colonIdx < 0) return null;

            int start = colonIdx + 1;
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t'))
                start++;

            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-'))
                end++;

            if (end > start && float.TryParse(json.Substring(start, end - start),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float val))
                return val;

            return null;
        }
    }
}
