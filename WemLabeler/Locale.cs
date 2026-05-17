using System.IO;
using System.Text.Json;

namespace WemLabeler;

public static class Locale
{
    public static string Language { get; private set; } = "zh-CN";

    private static readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static event Action? OnLanguageChanged;

    public static void SetLanguage(string lang)
    {
        if (lang != "zh-CN" && lang != "en-US")
            lang = "zh-CN";

        Language = lang;
        LoadLocale(lang);
        ConfigManager.SaveLocale(lang);
        OnLanguageChanged?.Invoke();
    }

    private static void LoadLocale(string lang)
    {
        _strings.Clear();

        var localePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "locales", $"{lang}.json");

        try
        {
            if (File.Exists(localePath))
            {
                var json = File.ReadAllText(localePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, _jsonOpts);
                if (dict != null)
                {
                    foreach (var kv in dict)
                        _strings[kv.Key] = kv.Value;
                }
            }
        }
        catch { }

        if (_strings.Count == 0)
        {
            foreach (var kv in GetFallbackStrings(lang))
                _strings[kv.Key] = kv.Value;
        }
    }

    public static string S(string key)
    {
        return _strings.TryGetValue(key, out var value) ? value : key;
    }

    public static string S(string key, params object[] args)
    {
        try
        {
            var fmt = S(key);
            return string.Format(fmt, args);
        }
        catch
        {
            return key;
        }
    }

    private static Dictionary<string, string> GetFallbackStrings(string lang)
    {
        return lang == "zh-CN" ? ZhCNFallback() : EnUSFallback();
    }

    private static Dictionary<string, string> ZhCNFallback()
    {
        return new Dictionary<string, string>
        {
            { "status_idle", "请通过 文件→打开CSV 加载WEM文件列表" },
            { "title_no_file", "WEM Labeler" },
            { "btn_play", "播放" },
            { "btn_stop", "停止" },
            { "btn_save", "保存标注" },
            { "btn_prev", "上一个 (←)" },
            { "btn_next", "下一个 (→)" },
            { "status_ready", "就绪" },
        };
    }

    private static Dictionary<string, string> EnUSFallback()
    {
        return new Dictionary<string, string>
        {
            { "status_idle", "Please open a CSV file via File→Open CSV" },
            { "title_no_file", "WEM Labeler" },
            { "btn_play", "Play" },
            { "btn_stop", "Stop" },
            { "btn_save", "Save Label" },
            { "btn_prev", "Prev (←)" },
            { "btn_next", "Next (→)" },
            { "status_ready", "Ready" },
        };
    }
}
