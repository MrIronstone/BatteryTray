using System.Diagnostics;
using HidSharp;
using HidSharp.Reports;

namespace BatteryTray;

internal enum DeviceKind { Headset, Mouse, Keyboard, Earbuds, Other }

/// Popup'ta bir satır: cihaz türü, adı, pil yüzdesi (yoksa null), şarj durumu ve kısa not.
/// Paneldeki alt satır her cihazda aynı biçimde: "[Şarj oluyor · | Dolu · ]Bağlantı"
/// Bağlantı her zaman "Kablosuz" ya da "USB kablo"; "Dolu" sadece kablodayken anlamlı.
internal static class Notes
{
    public const string Wireless = "Kablosuz";
    public const string Cable = "USB kablo";

    public static string Make(string conn, bool charging, bool full = false) =>
        charging ? $"Şarj oluyor · {conn}"
        : full && conn == Cable ? $"Dolu · {conn}"
        : conn;
}

/// Present = false: cihaz bu bilgisayarda yok, panelde gösterilmez.
internal sealed record DeviceStatus(DeviceKind Kind, string Name, int? Percent, bool Charging, string Note,
    bool Wired = false, bool Present = true);

/// Cihaz adları elle yazılmaz: USB'de cihazın kendini tanıttığı ad (product string) kullanılır.
internal static class HidNames
{
    public static string Of(HidDevice d, string fallback)
    {
        try
        {
            var n = d.GetProductName()?.Trim();
            return string.IsNullOrEmpty(n) ? fallback : n;
        }
        catch { return fallback; }
    }
}

// ---------------------------------------------------------------------------
// ASUS ROG fareler (VID 0x0B05) - G-Helper / BatteryTool'un çözdüğü protokol:
//  - Arayüz: genelde "mi_00"
//  - İstek (65 byte): [0]=report id (0) [1]=0x12 [2]=0x07
//  - Cevap: [1]=0x12 [2]=0x07 [5]=pil % (bazı eski modellerde x25) [10]=şarj (0 değilse şarjda)
//  - Cevap "FF AA" ise dongle cevap veriyor ama fare uykuda/kapalı
// ---------------------------------------------------------------------------
internal static class RogMouse
{
    private sealed record Profile(int Pid, string Name, string Iface = "mi_00",
        byte ReportId = 0, int Len = 65, int Offset = 5, int Scale = 1);

    private static readonly Profile[] Mice =
    {
        new(0x197F, "ROG Gladius III Wireless"), new(0x197D, "ROG Gladius III (kablo)"),
        new(0x1A72, "ROG Gladius III AimPoint"), new(0x1A70, "ROG Gladius III AimPoint (kablo)"),
        new(0x1B0C, "ROG Gladius III EVA-02"), new(0x1B0A, "ROG Gladius III EVA-02 (kablo)"),
        new(0x1ACE, "ROG Mouse (OMNI Receiver)", "mi_02&col03", 3, 64),
        new(0x1AD0, "ROG Harpe II", "mi_02&col03", 3, 64),
        new(0x1A1A, "ROG Chakram X"), new(0x1A18, "ROG Chakram X (kablo)"),
        new(0x1A94, "ROG Harpe Ace Aim Lab"), new(0x1A92, "ROG Harpe Ace Aim Lab (kablo)"),
        new(0x1A68, "ROG Keris AimPoint"), new(0x1A66, "ROG Keris AimPoint (kablo)"),
        new(0x1979, "ROG Spatha X"), new(0x1977, "ROG Spatha X (kablo)"),
        new(0x18A0, "ROG Gladius II Wireless", "mi_02", Scale: 25),
        new(0x1960, "ROG Keris Wireless", Scale: 25), new(0x195E, "ROG Keris (kablo)", Scale: 25),
    };

    private static bool _loggedOnce;

    public static DeviceStatus Read()
    {
        var all = DeviceList.Local.GetHidDevices(0x0B05).ToList();
        if (!_loggedOnce)
        {
            _loggedOnce = true;
            foreach (var d in all) Log.W($"[ROG] PID=0x{d.ProductID:X4} {d.DevicePath}");
        }

        DeviceStatus? last = null;
        foreach (var dev in all)
        {
            var p = Mice.FirstOrDefault(m => m.Pid == dev.ProductID &&
                dev.DevicePath.Contains(m.Iface, StringComparison.OrdinalIgnoreCase));
            if (p is null) continue;

            var r = Query(dev, p);
            if (r.Percent is not null) return r;
            last = r;
        }

        if (last is not null) return last;
        return all.Count == 0
            ? new(DeviceKind.Mouse, "Fare", null, false, "Bulunamadı", Present: false)
            : new(DeviceKind.Mouse, HidNames.Of(all[0], "ASUS fare"), null, false, "Desteklenmeyen model");
    }

    private static DeviceStatus Query(HidDevice dev, Profile p)
    {
        string name = HidNames.Of(dev, p.Name);
        bool cable = p.Name.Contains("(kablo)");
        try
        {
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            using var s = dev.Open(cfg);

            // Önce kuyrukta bekleyen eski paketleri boşalt
            s.ReadTimeout = 20;
            var buf = new byte[Math.Max(256, dev.GetMaxInputReportLength())];
            var drain = Stopwatch.StartNew();
            for (int i = 0; i < 16 && drain.ElapsedMilliseconds < 100; i++)
            {
                try { s.Read(buf); } catch (TimeoutException) { break; }
            }

            var q = new byte[p.Len];
            q[0] = p.ReportId; q[1] = 0x12; q[2] = 0x07;
            s.ReadTimeout = 300;
            s.WriteTimeout = 500;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                s.Write(q);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 900)
                {
                    int n;
                    try { n = s.Read(buf); } catch (TimeoutException) { continue; }
                    if (n < 11 || buf[0] != p.ReportId) continue;

                    if (buf[1] == 0xFF && buf[2] == 0xAA)
                        return new(DeviceKind.Mouse, name, null, false, "Uykuda / kapalı");

                    if (buf[1] == 0x12 && buf[2] == 0x07)
                    {
                        int pct = buf[p.Offset] * p.Scale;
                        if (pct is > 0 and <= 100)
                        {
                            bool charging = buf[10] != 0;
                            string note = Notes.Make(cable ? Notes.Cable : Notes.Wireless, charging);
                            return new(DeviceKind.Mouse, name, pct, charging, note);
                        }
                    }
                }
            }
            return new(DeviceKind.Mouse, name, null, false, "Cevap yok");
        }
        catch (Exception ex)
        {
            Log.W($"[ROG] {name} hata: {ex.Message}");
            return new(DeviceKind.Mouse, name, null, false, "Okunamadı");
        }
    }
}

// ---------------------------------------------------------------------------
// ASUS ROG kablosuz klavyeler (VID 0x0B05) - mikgangal/battery-tray'in çözdüğü protokol
// (ROG Strix Scope II 96 Wireless + Omni Receiver ile doğrulanmış):
//  - Arayüz: "mi_02&col02", vendor usage page 0xFF00, report ID 0x02
//  - İstek (64 byte): [0x02, 0x12, 0x01, 0...]
//  - Cevap: [1]=0x12 [2]=0x01 (yankı), [6]=pil %, [9]=şarj (1 = şarj oluyor)
// Başka ASUS cihazlarına (ör. anakart RGB kontrolcüsü) yanlışlıkla komut göndermemek için
// sadece hem bu arayüzü hem de bir klavye arayüzü olan cihazlar sorgulanır.
// ---------------------------------------------------------------------------
internal static class AsusKeyboard
{
    private const uint KeyboardUsage = 0x00010006;   // Generic Desktop / Keyboard
    private static bool _loggedOnce;

    private static bool HasUsage(HidDevice d, Func<uint, bool> match)
    {
        try { return d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues()).Any(match); }
        catch { return false; }
    }

    private static bool _diagnosed;

    /// Tanılama: bağlı TÜM ASUS HID arayüzlerini ayrıntılı loglar ve klavye arayüzü olan
    /// cihazların vendor kanallarına sadece okuma amaçlı pil sorguları gönderip cevapları yazar.
    /// İlk açılışta bir kez kendiliğinden, sonra menüden "ASUS tanılama" ile çalışır.
    public static void Diagnose()
    {
        Log.W("========== ASUS tanılama başladı ==========");
        var all = DeviceList.Local.GetHidDevices(0x0B05).ToList();
        if (all.Count == 0) { Log.W("[ASUS tanı] hiç ASUS HID cihazı yok"); return; }

        foreach (var d in all)
        {
            string usages = "?";
            string reports = "";
            try
            {
                var desc = d.GetReportDescriptor();
                usages = string.Join(",", desc.DeviceItems.SelectMany(i => i.Usages.GetAllValues())
                    .Distinct().Select(u => "0x" + u.ToString("X8")));
                reports = $" outIds=[{string.Join(",", desc.OutputReports.Select(r => r.ReportID))}]" +
                          $" inIds=[{string.Join(",", desc.InputReports.Select(r => r.ReportID))}]" +
                          $" featIds=[{string.Join(",", desc.FeatureReports.Select(r => r.ReportID))}]";
            }
            catch (Exception ex) { usages = "okunamadı: " + ex.Message; }

            string mfr = "?";
            try { mfr = d.GetManufacturer(); } catch { }
            Log.W($"[ASUS tanı] PID=0x{d.ProductID:X4} ad=\"{HidNames.Of(d, "?")}\" üretici=\"{mfr}\" " +
                  $"in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()} feat={d.GetMaxFeatureReportLength()}");
            Log.W($"            usages={usages}{reports}");
            Log.W($"            {d.DevicePath}");
        }

        // Sadece klavye arayüzü de olan cihazların vendor (0xFFxx) arayüzlerine sorgu gönder
        var keyboardPids = all.Where(d => HasUsage(d, u => u == KeyboardUsage)).Select(d => d.ProductID).ToHashSet();
        Log.W($"[ASUS tanı] klavye arayüzü olan PID'ler: {string.Join(",", keyboardPids.Select(p => "0x" + p.ToString("X4")))}");

        foreach (var d in all.Where(d => keyboardPids.Contains(d.ProductID) && d.GetMaxOutputReportLength() > 0
                                        && HasUsage(d, u => (u >> 16) >= 0xFF00)))
        {
            byte[] ids;
            try { ids = d.GetReportDescriptor().OutputReports.Select(r => r.ReportID).Distinct().ToArray(); }
            catch { ids = new byte[] { 0 }; }
            if (ids.Length == 0) ids = new byte[] { 0 };

            foreach (byte id in ids)
                foreach (byte sub in new byte[] { 0x01, 0x07 })   // 0x01: klavye pili, 0x07: fare pili
                    Probe(d, id, sub);
        }
        Log.W("========== ASUS tanılama bitti ==========");
    }

    private static void Probe(HidDevice d, byte reportId, byte sub)
    {
        string tag = $"[ASUS tanı] PID=0x{d.ProductID:X4} {ShortPath(d)} id=0x{reportId:X2} komut=12-{sub:X2}";
        try
        {
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            using var s = d.Open(cfg);
            s.ReadTimeout = 20;
            for (int i = 0; i < 16; i++) { try { s.Read(); } catch { break; } }

            var q = new byte[d.GetMaxOutputReportLength()];
            q[0] = reportId; q[1] = 0x12; q[2] = sub;
            s.WriteTimeout = 500;
            s.Write(q);

            s.ReadTimeout = 300;
            var sw = Stopwatch.StartNew();
            int got = 0;
            while (sw.ElapsedMilliseconds < 1000 && got < 4)
            {
                try { Log.W($"{tag} -> {Log.Hex(s.Read(), 16)}"); got++; }
                catch (TimeoutException) { }
            }
            if (got == 0) Log.W($"{tag} -> (cevap yok)");
        }
        catch (Exception ex) { Log.W($"{tag} -> hata: {ex.Message}"); }
    }

    private static string ShortPath(HidDevice d)
    {
        // "...&mi_02&col02#..." kısmını çıkar
        var p = d.DevicePath.ToLowerInvariant();
        int i = p.IndexOf("&mi_");
        if (i < 0) return "";
        int j = p.IndexOf('#', i);
        return j > i ? p[(i + 1)..j] : p[(i + 1)..];
    }

    public static DeviceStatus Read()
    {
        if (!_diagnosed) { _diagnosed = true; try { Diagnose(); } catch (Exception ex) { Log.W("[ASUS tanı] hata: " + ex); } }
        var all = DeviceList.Local.GetHidDevices(0x0B05).ToList();

        // Klavye arayüzü olan ASUS cihazları (PID bazında)
        var keyboardPids = all.Where(d => HasUsage(d, u => u == KeyboardUsage)).Select(d => d.ProductID).ToHashSet();

        var candidates = all.Where(d =>
                keyboardPids.Contains(d.ProductID) &&
                d.DevicePath.Contains("mi_02&col02", StringComparison.OrdinalIgnoreCase) &&
                d.GetMaxOutputReportLength() > 0 &&
                HasUsage(d, u => (u >> 16) == 0xFF00))
            .ToList();

        if (!_loggedOnce)
        {
            _loggedOnce = true;
            foreach (var d in candidates) Log.W($"[ASUS klavye] aday PID=0x{d.ProductID:X4} {d.DevicePath}");
        }

        DeviceStatus? last = null;
        foreach (var dev in candidates)
        {
            var r = Query(dev);
            if (r.Percent is not null) return r;
            last = r;
        }
        return last ?? new(DeviceKind.Keyboard, "ASUS klavye", null, false, "Bulunamadı", Present: false);
    }

    private static DeviceStatus Query(HidDevice dev)
    {
        // Alıcının adı ("ROG Omni Receiver") yerine varsa klavyenin kendi adını kullan
        string name = HidNames.Of(dev, "ASUS klavye");
        try
        {
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            using var s = dev.Open(cfg);

            // Kuyruktaki eski paketleri boşalt
            s.ReadTimeout = 20;
            var drain = Stopwatch.StartNew();
            for (int i = 0; i < 16 && drain.ElapsedMilliseconds < 100; i++)
            {
                try { s.Read(); } catch { break; }
            }

            var q = new byte[Math.Max(dev.GetMaxOutputReportLength(), 64)];
            q[0] = 0x02; q[1] = 0x12; q[2] = 0x01;
            s.ReadTimeout = 300;
            s.WriteTimeout = 500;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                s.Write(q);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 900)
                {
                    byte[] buf;
                    try { buf = s.Read(); } catch (TimeoutException) { continue; }
                    if (buf.Length < 10 || buf[0] != 0x02) continue;
                    if (buf[1] == 0xFF && buf[2] == 0xAA)
                        return new(DeviceKind.Keyboard, name, null, false, "Uykuda / kapalı");
                    if (buf[1] != 0x12 || buf[2] != 0x01) continue;

                    int pct = buf[6];
                    if (pct is <= 0 or > 100) continue;
                    bool charging = buf[9] == 1;
                    // Alıcı (Omni Receiver / dongle) üzerindense kablosuz, klavyenin kendi USB kimliğiyse kablo
                    bool viaReceiver = dev.ProductID == 0x1ACE ||
                        name.Contains("Receiver", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Dongle", StringComparison.OrdinalIgnoreCase);
                    return new(DeviceKind.Keyboard, name, pct, charging,
                        Notes.Make(viaReceiver ? Notes.Wireless : Notes.Cable, charging));
                }
            }
            return new(DeviceKind.Keyboard, name, null, false, "Cevap yok");
        }
        catch (Exception ex)
        {
            Log.W($"[ASUS klavye] {name} hata: {ex.Message}");
            return new(DeviceKind.Keyboard, name, null, false, "Okunamadı");
        }
    }
}

// ---------------------------------------------------------------------------
// Keychron K2 Max (VID 0x3434, 2.4 GHz alıcı PID 0xD030)
// Stok firmware pil bilgisini 2.4 GHz'de paylaşmıyor ve alıcı bilgisayardan
// klavyeye giden istekleri iletmiyor. Bu yüzden klavyeye yamalı bir QMK
// firmware'i yüklüyoruz: klavye birkaç saniyede bir raw HID kanalından (0xFF60)
// kendiliğinden şu paketi gönderiyor ("push"):
//   [0xA4, yüzde, voltaj_lo, voltaj_hi, şarj (0 pil / 1 şarj / 2 dolu), transport]
// Uygulama bu kanalı arka planda sürekli dinliyor.
// ---------------------------------------------------------------------------
internal static class Keychron
{
    // Ad cihazdan gelir: kabloda klavyenin kendi adı, 2.4 GHz'de alıcının adı
    private static volatile string Name = "Klavye";
    private static Thread? _listener;
    private static volatile bool _dongleFound;
    private static (int pct, int mv, int charge, DateTime at)? _last;
    private static readonly object _lock = new();

    public static DeviceStatus Read()
    {
        if (_listener is null)
        {
            _listener = new Thread(Listen) { IsBackground = true, Name = "KeychronListener" };
            _listener.Start();
        }

        // Kablo (Cable) modu: klavye doğrudan USB cihazı olarak görünür, pili sorarak alırız
        var wired = ReadWired();
        if (wired is not null) return wired;

        (int pct, int mv, int charge, DateTime at)? last;
        lock (_lock) last = _last;

        if (last is null)
            return _dongleFound
                ? new(DeviceKind.Keyboard, Name, null, false, "Pil paketi bekleniyor")
                : new(DeviceKind.Keyboard, Name, null, false, "Bulunamadı", Present: false);

        var v = last.Value;
        var age = DateTime.Now - v.at;
        bool charging = v.charge == 1;
        if (age.TotalSeconds > 60)
            // Klavye uykuya geçince paket gelmez; son değeri saatiyle göster
            return new(DeviceKind.Keyboard, Name, v.pct, false, $"Uykuda · son {v.at:HH:mm}");

        return Build(v.pct, v.charge, Notes.Wireless);
    }

    private static bool _wiredLogged;

    // Şarj olurken pil voltajı yapay olarak yüksek okunur (~4.15 V) ve yüzde %100 görünür.
    // Bu yüzden şarj sırasında voltajdan gelen yüzdeyi değil, şarjdan önceki son
    // güvenilir ölçümü gösteriyoruz.
    // Son güvenilir (pildeyken) ölçüm, uygulama kapanıp açılınca kaybolmasın diye dosyada tutulur
    private static readonly string StateFile = System.IO.Path.Combine(AppContext.BaseDirectory, "keychron_last.txt");
    private static (int pct, DateTime at)? _lastOnBattery = LoadLast();

    private static (int pct, DateTime at)? LoadLast()
    {
        try
        {
            var parts = File.ReadAllText(StateFile).Split('|');
            return (int.Parse(parts[0]), DateTime.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { return null; }
    }

    private static void SaveLast(int pct, DateTime at)
    {
        try { File.WriteAllText(StateFile, $"{pct}|{at.ToString("o", System.Globalization.CultureInfo.InvariantCulture)}"); }
        catch { }
    }

    private static DeviceStatus Build(int pct, int charge, string conn)
    {
        if (charge == 0)
        {
            var now = DateTime.Now;
            lock (_lock) _lastOnBattery = (pct, now);
            SaveLast(pct, now);
        }

        if (charge == 2) return new(DeviceKind.Keyboard, Name, 100, false, Notes.Make(conn, false, full: true));
        if (charge == 1)
        {
            (int pct, DateTime at)? b;
            lock (_lock) b = _lastOnBattery;
            return b is null
                ? new(DeviceKind.Keyboard, Name, null, true, Notes.Make(conn, true))
                : new(DeviceKind.Keyboard, Name, b.Value.pct, true, Notes.Make(conn, true));
        }
        return new(DeviceKind.Keyboard, Name, pct, false, conn);
    }

    /// Kablo modunda klavyenin kendi raw HID arayüzüne 0xA4 (pil) isteği gönderir.
    private static DeviceStatus? ReadWired()
    {
        var dev = DeviceList.Local.GetHidDevices(0x3434)
            .Where(d => d.ProductID != 0xD030 && d.GetMaxOutputReportLength() > 0)
            .FirstOrDefault(IsRawHid);
        if (dev is null) return null;
        Name = HidNames.Of(dev, Name);

        try
        {
            var cfg = new OpenConfiguration();
            cfg.SetOption(OpenOption.Exclusive, false);
            using var s = dev.Open(cfg);
            s.ReadTimeout = 300;
            var q = new byte[Math.Max(dev.GetMaxOutputReportLength(), 33)];
            q[0] = 0x00; q[1] = 0xA4;
            s.Write(q);

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                byte[] buf;
                try { buf = s.Read(); } catch (TimeoutException) { continue; }
                int i = buf.Length > 0 && buf[0] == 0xA4 ? 0 : buf.Length > 1 && buf[1] == 0xA4 ? 1 : -1;
                if (i < 0 || buf.Length < i + 6) continue;

                int pct = buf[i + 1], charge = buf[i + 4];
                if (!_wiredLogged)
                {
                    _wiredLogged = true;
                    Log.W($"[Keychron] kablo modu PID=0x{dev.ProductID:X4}: %{pct}, şarj={charge}, ham={Log.Hex(buf, 10)}");
                }
                if (pct is <= 0 or > 100) return new(DeviceKind.Keyboard, Name, null, false, "USB kablo · pil okunamadı");
                // Kablodayken yüzde güvenilir değil (şarj voltajı): yüzde yerine priz simgesi göster
                return new(DeviceKind.Keyboard, Name, null, charge == 1, Notes.Cable, Wired: true);
            }
            return new(DeviceKind.Keyboard, Name, null, false, "USB kablo · cevap yok (firmware?)");
        }
        catch (Exception ex)
        {
            Log.W("[Keychron] kablo modu hatası: " + ex.Message);
            return null;
        }
    }

    private static bool IsRawHid(HidDevice d)
    {
        try
        {
            return d.GetReportDescriptor().DeviceItems
                .SelectMany(i => i.Usages.GetAllValues())
                .Any(u => (u >> 16) == 0xFF60);
        }
        catch { return false; }
    }

    private static void Listen()
    {
        while (true)
        {
            try
            {
                var dev = DeviceList.Local.GetHidDevices(0x3434)
                    .FirstOrDefault(d => d.DevicePath.Contains("mi_03", StringComparison.OrdinalIgnoreCase));
                _dongleFound = dev is not null;
                if (dev is null) { Thread.Sleep(5000); continue; }
                Name = HidNames.Of(dev, Name);

                var cfg = new OpenConfiguration();
                cfg.SetOption(OpenOption.Exclusive, false);
                using var s = dev.Open(cfg);
                s.ReadTimeout = 15000;
                Log.W("[Keychron] raw HID dinleniyor");
                bool firstLogged = false;

                while (true)
                {
                    byte[] buf;
                    try { buf = s.Read(); }   // alıcı çıkarılırsa IOException, dış döngü yeniden bağlanır
                    catch (TimeoutException) { continue; }
                    catch (IOException ex) when (ex.Message.Contains("after some time")) { continue; }
                    // Report ID (0) başta olabilir: 0xA4'ü ilk iki baytta ara
                    int i = buf.Length > 0 && buf[0] == 0xA4 ? 0 : buf.Length > 1 && buf[1] == 0xA4 ? 1 : -1;
                    if (i < 0 || buf.Length < i + 6) continue;

                    int pct = buf[i + 1];
                    int mv = buf[i + 2] | (buf[i + 3] << 8);
                    int charge = buf[i + 4];
                    if (pct is < 0 or > 100) continue;

                    lock (_lock) _last = (pct, mv, charge, DateTime.Now);
                    if (!firstLogged)
                    {
                        firstLogged = true;
                        Log.W($"[Keychron] ilk pil paketi: %{pct}, {mv} mV, şarj={charge}, ham={Log.Hex(buf, 10)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.W("[Keychron] dinleme hatası: " + ex.Message);
                Thread.Sleep(5000);
            }
        }
    }
}
