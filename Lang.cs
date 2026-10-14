using System.Globalization;
using Microsoft.Win32;

namespace BatteryTray;

/// Arayüz dili. "Otomatik" Windows'un görüntüleme diline bakar (Türkçe ise Türkçe, değilse İngilizce).
/// Seçim HKCU\Software\BatteryTray\Language altında saklanır.
internal static class L
{
    public enum Lang { Auto, Tr, En }

    private const string Key = @"Software\BatteryTray";
    public static Lang Setting { get; private set; } = Load();

    public static bool IsTr =>
        Setting == Lang.Tr ||
        (Setting == Lang.Auto && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr");

    /// Türkçe ve İngilizce metinden o anki dile uygun olanı döndürür.
    public static string T(string tr, string en) => IsTr ? tr : en;

    /// Yüzde biçimi: Türkçede "%87", İngilizcede "87%".
    public static string Pct(int p) => IsTr ? $"%{p}" : $"{p}%";
    public static string Pct(double p, string fmt) => IsTr ? "%" + p.ToString(fmt) : p.ToString(fmt) + "%";

    public static void Set(Lang lang)
    {
        Setting = lang;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            k.SetValue("Language", lang.ToString());
        }
        catch { /* kaydedilemezse sadece bu oturum için geçerli olur */ }
    }

    private static Lang Load()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k?.GetValue("Language") is string s && Enum.TryParse<Lang>(s, out var l)) return l;
        }
        catch { }
        return Lang.Auto;
    }
}
