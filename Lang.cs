using System.Globalization;
using Microsoft.Win32;

namespace BatteryTray;

/// UI language. English by default; "Automatic" follows the Windows display language (Turkish if Turkish, otherwise English).
/// The choice is stored under HKCU\Software\BatteryTray\Language.
internal static class L
{
    public enum Lang { Auto, Tr, En }

    private const string Key = @"Software\BatteryTray";
    public static Lang Setting { get; private set; } = Load();

    public static bool IsTr =>
        Setting == Lang.Tr ||
        (Setting == Lang.Auto && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr");

    /// Returns the Turkish or English text depending on the current language.
    public static string T(string tr, string en) => IsTr ? tr : en;

    /// Percent format: "%87" in Turkish, "87%" in English.
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
        catch { /* if it can't be saved, it only applies to this session */ }
    }

    private static Lang Load()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            if (k?.GetValue("Language") is string s && Enum.TryParse<Lang>(s, out var l)) return l;
        }
        catch { }
        return Lang.En;   // English by default
    }
}
