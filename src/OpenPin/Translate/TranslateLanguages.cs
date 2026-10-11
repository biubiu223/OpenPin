using OpenPin.Core;

namespace OpenPin.Translate;

/// <summary>A target language: OpenPin tag (BCP-47 style), English name (used in LLM prompts) and its per-provider codes.</summary>
public sealed record TranslateLanguage(string Tag, string EnglishName, string Baidu, string Youdao, string? DeepL)
{
    /// <summary>Localized display name (Translate.Lang.&lt;tag&gt;).</summary>
    public string DisplayName => Loc.Get("Translate.Lang." + Tag);
}

/// <summary>Supported languages, provider code mapping, "follow app" resolution and simple script/heuristic detection.</summary>
public static class TranslateLanguages
{
    /// <summary>Supported target languages (PRO-01 list, in display order). "Follow app" is the empty tag and not part of this list.</summary>
    public static readonly IReadOnlyList<TranslateLanguage> All = new TranslateLanguage[]
    {
        new("zh-Hans", "Simplified Chinese", "zh", "zh-CHS", "ZH-HANS"),
        new("zh-Hant", "Traditional Chinese", "cht", "zh-CHT", "ZH-HANT"),
        new("en", "English", "en", "en", "EN-US"),
        new("ja", "Japanese", "jp", "ja", "JA"),
        new("ko", "Korean", "kor", "ko", "KO"),
        new("fr", "French", "fra", "fr", "FR"),
        new("de", "German", "de", "de", "DE"),
        new("es", "Spanish", "spa", "es", "ES"),
        new("pt", "Portuguese", "pt", "pt", "PT-BR"),
        new("id", "Indonesian", "id", "id", "ID"),
        new("ms", "Malay", "may", "ms", null),
        new("it", "Italian", "it", "it", "IT"),
        new("ru", "Russian", "ru", "ru", "RU"),
        new("th", "Thai", "th", "th", "TH"),
        new("vi", "Vietnamese", "vie", "vi", "VI"),
        new("ar", "Arabic", "ara", "ar", "AR"),
        new("tr", "Turkish", "tr", "tr", "TR"),
    };

    public static TranslateLanguage? Find(string? tag) =>
        string.IsNullOrWhiteSpace(tag) ? null : All.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Map an app UI culture ("zh-CN", "zh-TW", "en-US"…) to a supported tag. Unknown → zh-Hans.</summary>
    public static string FromCulture(string? culture)
    {
        var c = (culture ?? "").Trim();
        if (c.Length == 0) return "zh-Hans";
        if (c.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var lower = c.ToLowerInvariant();
            return lower.Contains("hant") || lower.EndsWith("-tw") || lower.EndsWith("-hk") || lower.EndsWith("-mo") ? "zh-Hant" : "zh-Hans";
        }
        var primary = c.Split('-', '_')[0];
        return Find(primary)?.Tag ?? "zh-Hans";
    }

    /// <summary>The configured target, with "" (follow app) resolved to the UI language.</summary>
    public static string ResolveTarget(string? target, string? appCulture = null)
    {
        if (Find(target) is { } l) return l.Tag;
        appCulture ??= TryAppCulture();
        return FromCulture(appCulture);
    }

    private static string? TryAppCulture()
    {
        try { return AppServices.Settings?.Current.General.Language; }
        catch { return null; }
    }

    /// <summary>
    /// Target actually used for <paramref name="text"/>: when the detected source language equals the target, the
    /// fallback language is used instead (e.g. target Chinese, text already Chinese → translate to English).
    /// </summary>
    public static string EffectiveTarget(string text, string target, string? fallback)
    {
        var detected = Detect(text);
        if (detected != null && SameLanguage(detected, target) && Find(fallback) is { } fb && !SameLanguage(fb.Tag, target))
            return fb.Tag;
        return target;
    }

    public static bool SameLanguage(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        (IsMalayLike(a) && IsMalayLike(b));

    private static bool IsMalayLike(string t) => t is "id" or "ms";

    // ------------------------------------------------------------------ detection

    /// <summary>
    /// Very small script/heuristic detector: CJK scripts, Hangul, kana, Cyrillic, Arabic, Thai by Unicode block; Latin
    /// languages by distinctive letters and stop words. Returns a tag of <see cref="All"/> or null when unsure.
    /// </summary>
    public static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int han = 0, kana = 0, hangul = 0, cyr = 0, arabic = 0, thai = 0, latin = 0;
        foreach (var c in text)
        {
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿' or >= '豈' and <= '﫿') han++;
            else if (c is >= '぀' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ') kana++;
            else if (c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏') hangul++;
            else if (c is >= 'Ѐ' and <= 'ӿ') cyr++;
            else if (c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ') arabic++;
            else if (c is >= '฀' and <= '๿') thai++;
            else if (char.IsLetter(c) && c < 'ɐ' || c is >= 'Ḁ' and <= 'ỿ') latin++;
        }
        if (han + kana + hangul + cyr + arabic + thai + latin == 0) return null;

        if (kana > 0 && kana * 10 >= han + kana) return "ja";
        // One ideograph / syllable block carries about as much as a short word: weight them against letters.
        var scripts = new (double Weight, string Tag)[]
        {
            (han * 3.0, "zh"), (hangul * 2.0, "ko"), (cyr, "ru"), (arabic, "ar"), (thai, "th"), (latin, "latin"),
        };
        double sum = scripts.Sum(s => s.Weight);
        var top = scripts.OrderByDescending(s => s.Weight).First();
        if (top.Weight * 2 < sum) return null; // no dominant script
        return top.Tag switch
        {
            "zh" => DetectChineseVariant(text),
            "latin" => DetectLatin(text),
            _ => top.Tag,
        };
    }

    // Characters that exist (in common use) in only one of the two Chinese scripts.
    private const string TraditionalOnly = "這個們來時說會對國學發後過還現開關於與東車長門問間見電話經點機業體頭變專讓當區實應樣歡請記號邊認幾無麼從萬給興習寫買賣錢準備滿氣書親顯總網際導灣華語麗陽陰飛鳥馬魚圖讀線員裡區議題據計輸頁鍵設標選擇資訊數統檔傳";
    private const string SimplifiedOnly = "这个们来时说会对国学发后过还现开关于与东车长门问间见电话经点机业体头变专让当区实应样欢请记号边认几无么从万给兴习写买卖钱准备满气书亲显总网际导湾华语丽阳阴飞鸟马鱼图读线员里区议题据计输页键设标选择资讯数统档传";

    private static string DetectChineseVariant(string text)
    {
        int trad = 0, simp = 0;
        foreach (var c in text)
        {
            if (TraditionalOnly.IndexOf(c) >= 0) trad++;
            else if (SimplifiedOnly.IndexOf(c) >= 0) simp++;
        }
        return trad > simp ? "zh-Hant" : "zh-Hans";
    }

    private static readonly Dictionary<string, string[]> StopWords = new()
    {
        ["en"] = new[] { "the", "and", "of", "to", "is", "in", "for", "with", "that", "this", "are", "on", "you", "it", "be", "was", "or", "not", "can", "your", "from", "have", "will", "an", "by", "at" },
        ["fr"] = new[] { "le", "la", "les", "des", "et", "est", "une", "un", "du", "dans", "pour", "pas", "que", "qui", "sur", "avec", "vous", "nous", "ce", "au" },
        ["de"] = new[] { "der", "die", "das", "und", "ist", "nicht", "ein", "eine", "mit", "den", "von", "zu", "auf", "für", "sie", "ich", "wir", "auch", "dem", "es" },
        ["es"] = new[] { "el", "la", "los", "las", "y", "es", "de", "que", "en", "un", "una", "por", "con", "para", "del", "se", "no", "su", "como", "al" },
        ["pt"] = new[] { "o", "a", "os", "as", "e", "é", "de", "que", "em", "um", "uma", "para", "com", "não", "do", "da", "no", "na", "se", "por" },
        ["it"] = new[] { "il", "la", "le", "di", "e", "è", "che", "un", "una", "per", "con", "non", "del", "della", "gli", "sono", "nel", "alla", "lo", "si" },
        ["id"] = new[] { "yang", "dan", "di", "ini", "itu", "dengan", "untuk", "dari", "tidak", "ke", "akan", "pada", "adalah", "kami", "anda", "juga", "ada", "atau", "saya", "bisa" },
        ["tr"] = new[] { "ve", "bir", "bu", "da", "de", "için", "ile", "çok", "ne", "olarak", "daha", "gibi", "değil", "var", "ben", "sen", "mi", "ama", "en", "kadar" },
        ["vi"] = new[] { "và", "của", "là", "có", "không", "được", "cho", "những", "một", "các", "này", "với", "người", "trong", "đã", "để", "khi", "từ", "như", "bạn" },
    };

    private static string? DetectLatin(string text)
    {
        var lower = text.ToLowerInvariant();
        // Distinctive letters first.
        if (lower.IndexOfAny("ơưđăạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹ".ToCharArray()) >= 0) return "vi";
        if (lower.IndexOfAny("ğış".ToCharArray()) >= 0) return "tr";
        if (lower.IndexOf('ß') >= 0) return "de";
        if (lower.IndexOfAny("ñ¿¡".ToCharArray()) >= 0) return "es";
        if (lower.IndexOfAny("ãõ".ToCharArray()) >= 0) return "pt";

        var words = System.Text.RegularExpressions.Regex.Matches(lower, @"\p{L}+").Select(m => m.Value).ToList();
        if (words.Count == 0) return null;
        string? best = null;
        int bestScore = 0;
        foreach (var (lang, list) in StopWords)
        {
            int score = words.Count(w => Array.IndexOf(list, w) >= 0);
            if (score > bestScore) { best = lang; bestScore = score; }
        }
        // Too few signals: plain ASCII text of a few words is most likely English (UI labels, code…).
        if (bestScore == 0 || (bestScore < 2 && words.Count > 3)) return words.All(w => w.All(c => c < 128)) ? "en" : null;
        return best;
    }
}
