using System;
using System.Collections.Generic;
using System.Globalization;

namespace Valve.Core
{
    /// <summary>Dictionary-based localization infrastructure (ledger 27.01 / 22.15).
    /// Defaults to the OS UI language; unknown keys fall back to English. Per-string extraction
    /// is tracked in FEATURE_LEDGER.md Section 27; this class is the runtime the extraction feeds.</summary>
    public static class Localization
    {
        public static string CurrentLanguage { get; private set; } = "en";

        private static readonly Dictionary<string, string> En = new(StringComparer.OrdinalIgnoreCase)
        {
            ["app.title"] = "Valve",
            ["menu.file"] = "File",
            ["menu.tools"] = "Tools",
            ["menu.window"] = "Window",
            ["menu.help"] = "Help",
            ["tab.activity"] = "Activity",
            ["tab.history"] = "History",
            ["tab.stats"] = "Stats",
            ["status.online"] = "Online",
            ["status.blocked"] = "Blocked",
            ["status.limited"] = "Limited",
            ["btn.settings"] = "Settings…",
            ["btn.exit"] = "Exit",
            ["about.version"] = "Valve v" + Valve.Api.ValveApi.Version + " — Windows traffic limiter",
        };

        private static readonly Dictionary<string, string> Zh = new(StringComparer.OrdinalIgnoreCase)
        {
            ["app.title"] = "Valve",
            ["menu.file"] = "文件",
            ["menu.tools"] = "工具",
            ["menu.window"] = "窗口",
            ["menu.help"] = "帮助",
            ["tab.activity"] = "活动",
            ["tab.history"] = "历史",
            ["tab.stats"] = "统计",
            ["status.online"] = "在线",
            ["status.blocked"] = "已阻止",
            ["status.limited"] = "已限速",
            ["btn.settings"] = "设置…",
            ["btn.exit"] = "退出",
            ["about.version"] = "Valve v" + Valve.Api.ValveApi.Version + " — Windows 流量限制器",
        };

        public static void Init()
        {
            string lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            CurrentLanguage = lang is "zh" or "cs" or "fr" or "de" or "ru" or "ja" or "ko" ? lang : "en";
        }

        public static string T(string key)
        {
            if (CurrentLanguage == "zh" && Zh.TryGetValue(key, out var zh)) return zh;
            return En.TryGetValue(key, out var en) ? en : key;
        }
    }
}
