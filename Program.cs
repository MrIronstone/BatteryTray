using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using HidSharp;

namespace BatteryTray;

// ---------------------------------------------------------------------------
// HS80 MAX protokolü (topluluk tarafından tersine mühendislikle çözüldü):
//  - VID 0x1B1C (Corsair), kablosuz dongle PID 0x0A97
//  - Kontrol arayüzü: interface 4 (yolda "mi_04"), vendor usage page 0xFF42
//  - İstek  (64 byte): [0]=0x02 (report id) [1]=hedef [2]=0x02 (GET) [3]=özellik
//      hedef 0x08 = dongle/kök cihaz, 0x09 = kulaklık (child)
//      özellik 0x0F = pil (LE16, 0..1000 -> /10 = %)
//      özellik 0x10 = şarj durumu (0 deşarj, 1 şarj oluyor, 2 dolu)
//  - Cevap: [1]=hedef [2]=komut [3]=hata kodu (0 = OK) [4..]=değer
// ---------------------------------------------------------------------------
internal static class Hs80Protocol
{
    public const int CorsairVid = 0x1B1C;
    public const uint VendorUsagePage = 0xFF42;
    public const byte ReportId = 0x02;
    public const byte RouteRoot = 0x08;
    public const byte RouteHeadset = 0x09;
    public const byte CmdGet = 0x02;
    public const byte PropBattery = 0x0F;
    public const byte PropChargeState = 0x10;
}

internal sealed record BatteryInfo(double Percent, int ChargeState);

internal static class Log
{
    // Log, exe ile aynı klasörde tutulur (batterytray_log.txt)
    public static readonly string File_ = System.IO.Path.Combine(AppContext.BaseDirectory, "batterytray_log.txt");
    public static void W(string msg)
    {
        try
        {
            // Log 2 MB'ı geçerse sıfırla
            var fi = new FileInfo(File_);
            if (fi.Exists && fi.Length > 2_000_000) File.WriteAllText(File_, "");
            File.AppendAllText(File_, $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { }
    }
    public static string Hex(byte[] b, int n = 16) => BitConverter.ToString(b, 0, Math.Min(n, b.Length));
}

internal sealed class Hs80Device : IDisposable
{
    public string? LastError { get; private set; }
    private HidStream? _stream;
    private int _outLen = 64;
    private byte _route = Hs80Protocol.RouteHeadset;
    private byte _reportId = Hs80Protocol.ReportId;
    private bool _discovering;   // paket logu sadece cihaz aranırken yazılır

    public string? Path { get; private set; }
    public string? ProductName { get; private set; }   // cihazın USB'de bildirdiği ad
    public bool Found { get; private set; }            // bu bilgisayarda desteklenen kulaklık var mı

    /// Corsair'in vendor (0xFF42) kontrol arayüzünü bulur. PID'e bakmıyoruz,
    /// böylece hem dongle (0x0A97) hem de kabloyla bağlı kulaklık denenebilir.
    public static IEnumerable<HidDevice> FindCandidates()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(Hs80Protocol.CorsairVid))
        {
            if (HasVendorUsagePage(d)) yield return d;
        }
    }

    public static bool HasVendorUsagePage(HidDevice d)
    {
        try
        {
            var desc = d.GetReportDescriptor();
            foreach (var item in desc.DeviceItems)
                foreach (var usage in item.Usages.GetAllValues())
                    if ((usage >> 16) == Hs80Protocol.VendorUsagePage) return true;
        }
        catch { /* bazı arayüzler descriptor vermez */ }
        return false;
    }

    public bool EnsureOpen()
    {
        if (_stream != null) return true;
        _discovering = true;
        try { return EnsureOpenCore(); }
        finally { _discovering = false; }
    }

    private bool EnsureOpenCore()
    {

        // Yazılabilir (out > 0) arayüzleri al; bilinen çalışan dongle (0x0A97)
        // önce, kulaklığın kendi USB arayüzü (0x0A96) sonra denensin.
        var candidates = FindCandidates()
            .Where(d => d.GetMaxOutputReportLength() > 0)
            .OrderBy(d => d.ProductID == 0x0A97 ? 0 : 1)
            .ToList();
        Log.W($"aday arayüz sayısı: {candidates.Count}");
        Found = candidates.Count > 0;
        if (!Found) LastError = L.T("Bulunamadı", "Not found");

        foreach (var dev in candidates)
        {
          try
          {
            Log.W($"deneniyor PID=0x{dev.ProductID:X4} out={dev.GetMaxOutputReportLength()} in={dev.GetMaxInputReportLength()} {dev.DevicePath}");
            if (!dev.TryOpen(out var s)) { LastError = "arayüz açılamadı"; Log.W("  açılamadı"); continue; }
            s.ReadTimeout = 400;
            s.WriteTimeout = 400;
            _stream = s;
            _outLen = Math.Max(dev.GetMaxOutputReportLength(), 64);
            Path = dev.DevicePath;
            // Alıcı kendini "... Receiver" diye tanıtır; panelde kulaklığı temsil ettiği için "Headset" yaz
            ProductName = HidNames.Of(dev, L.T("Kulaklık", "Headset")).Replace("Receiver", "Headset");

            // Cihazın tanımladığı output report ID'lerini oku, 0x02 yoksa ilkini kullan.
            try
            {
                var ids = dev.GetReportDescriptor().OutputReports.Select(r => r.ReportID).ToList();
                Log.W("  output report id'leri: " + string.Join(",", ids.Select(i => "0x" + i.ToString("X2"))));
                _reportId = ids.Count == 0 || ids.Contains(Hs80Protocol.ReportId) ? Hs80Protocol.ReportId : ids[0];
            }
            catch (Exception ex) { Log.W("  descriptor okunamadı: " + ex.Message); }

            // Önce kulaklık (0x09) hedefini dene; kabloyla doğrudan bağlıysa
            // kulaklığın kendisi kök (0x08) olarak cevap verebilir.
            foreach (var route in new[] { Hs80Protocol.RouteHeadset, Hs80Protocol.RouteRoot })
            {
                _route = route;
                Log.W($"  hedef 0x{route:X2} deneniyor");
                if (route == Hs80Protocol.RouteHeadset) Wake();
                if (ReadBatteryRaw() is not null) { Log.W("  OK"); LastError = null; return true; }
            }
            LastError = L.T("kulaklıktan geçerli cevap gelmedi", "no valid response from headset");
          }
          catch (Exception ex)
          {
            LastError = ex.GetType().Name + ": " + ex.Message;
            Log.W($"  hata (PID=0x{dev.ProductID:X4}): " + ex);
          }
          Close();
        }
        return false;
    }

    public BatteryInfo? Read()
    {
        try
        {
            if (!EnsureOpen()) return null;
            var raw = ReadBatteryRaw();
            if (raw is null && _route == Hs80Protocol.RouteHeadset)
            {
                // Kablosuzda kulaklık bir süre sessiz kalınca uyandırmak gerekebiliyor
                Wake();
                raw = ReadBatteryRaw();
            }
            if (raw is null)
            {
                // Kablo takıldı/çıkarıldıysa doğru arayüz değişmiş olabilir:
                // bağlantıyı kapat, bir sonraki turda baştan arasın.
                LastError = L.T("cevap alınamadı, yeniden aranacak", "no response, will retry");
                Close();
                return null;
            }
            var charge = GetProperty(Hs80Protocol.PropChargeState);
            int state = charge is { Length: > 0 } ? charge[0] : -1;
            return new BatteryInfo(raw.Value / 10.0, state);
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            Log.W("okuma hatası: " + ex);
            Close(); // cihaz çıkarıldıysa bir sonraki turda yeniden ara
            return null;
        }
    }

    /// HeadsetControl'deki el sıkışma: alıcıya firmware sorgusu (0x13) ve "heartbeat" (0x12),
    /// ardından kulaklığa heartbeat. Kulaklığı yazılım moduna almadan konuşmayı açar.
    private void Wake()
    {
        if (_stream == null) return;
        void Send(byte route, byte prop)
        {
            var req = new byte[_outLen];
            req[0] = _reportId; req[1] = route; req[2] = Hs80Protocol.CmdGet; req[3] = prop;
            _stream!.Write(req);
        }
        void Drain()
        {
            int old = _stream!.ReadTimeout;
            _stream.ReadTimeout = 50;
            try { for (int i = 0; i < 10; i++) { var r = _stream.Read(); if (_discovering) Log.W($"    < (wake) {Log.Hex(r)}"); } }
            catch { }
            _stream.ReadTimeout = old;
        }
        try
        {
            if (_discovering) Log.W("    wake: alıcı 0x13/0x12, kulaklık 0x12");
            Send(Hs80Protocol.RouteRoot, 0x13);
            Send(Hs80Protocol.RouteRoot, 0x12);
            Drain();
            Send(Hs80Protocol.RouteHeadset, 0x12);
            Thread.Sleep(100);
            Drain();
        }
        catch (Exception ex) { Log.W("    wake hatası: " + ex.Message); }
    }

    private int? ReadBatteryRaw()
    {
        var v = GetProperty(Hs80Protocol.PropBattery);
        if (v is not { Length: >= 2 }) return null;
        int raw = v[0] | (v[1] << 8);
        return raw is > 0 and <= 1000 ? raw : null;
    }

    /// GET isteği gönderir, eşleşen cevabın değer kısmını döner.
    private byte[]? GetProperty(byte prop)
    {
        if (_stream == null) return null;

        var req = new byte[_outLen];
        req[0] = _reportId;
        req[1] = _route;
        req[2] = Hs80Protocol.CmdGet;
        req[3] = prop;
        if (_discovering) Log.W($"    > {Log.Hex(req, 8)}");
        _stream.Write(req);

        // Arada kulaklığın kendiliğinden gönderdiği olay paketleri gelebilir,
        // bu yüzden birkaç rapor okuyup bizimkini arıyoruz.
        for (int i = 0; i < 8; i++)
        {
            byte[] resp;
            try { resp = _stream.Read(); }
            catch (Exception ex) when (ex is TimeoutException || (ex is IOException && ex.Message.Contains("after some time")))
            { if (_discovering) Log.W("    < (zaman aşımı)"); return null; }
            if (_discovering) Log.W($"    < {Log.Hex(resp)}");

            // Report ID bazen başta olur bazen olmaz: iki ofseti de dene.
            foreach (int off in new[] { 0, -1 })
            {
                int r = 1 + off;
                if (r < 0 || resp.Length < r + 5) continue;
                // Kablolu modda kulaklık hedef baytını 0x00 olarak döndürüyor,
                // bu yüzden hedefi değil sadece komut + hata kodunu kontrol ediyoruz.
                if (resp[r + 1] == Hs80Protocol.CmdGet && resp[r + 2] == 0x00)
                    return resp[(r + 3)..];
            }
        }
        return null;
    }

    public void Close()
    {
        _stream?.Dispose();
        _stream = null;
    }

    public void Dispose() => Close();
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Hs80Device _device = new();
    private readonly PopupForm _popup = new();
    // Başlangıçta hiçbiri gösterilmez; cihaz bulununca adıyla birlikte panele eklenir
    private DeviceStatus _headset = new(DeviceKind.Headset, L.T("Kulaklık", "Headset"), null, false, "...", Present: false);
    private DeviceStatus _mouse = new(DeviceKind.Mouse, L.T("Fare", "Mouse"), null, false, "...", Present: false);
    private DeviceStatus _keyboard = new(DeviceKind.Keyboard, L.T("Klavye", "Keyboard"), null, false, "...", Present: false);
    private DeviceStatus _asusKeyboard = new(DeviceKind.Keyboard, L.T("ASUS klavye", "ASUS keyboard"), null, false, "...", Present: false);
    private readonly System.Windows.Forms.Timer _timer;
    private Icon? _currentIcon;
    private bool _busy;

    // Pil simgesi durumu
    private readonly System.Windows.Forms.Timer _blink;
    private int _pct;
    private int _state = -1;
    private bool _blinkOn;

    public TrayApp()
    {
        _tray = new NotifyIcon { ContextMenuStrip = BuildMenu(), Visible = true, Text = L.T("Cihazlar aranıyor...", "Looking for devices...") };
        SetIcon("?", Color.Gray);
        _tray.MouseClick += OnTrayClick;
        _popup.NearbyProvider = AirPods.Nearby;
        AirPods.Start();   // yakındaki cihazlar sekmesi için tarama hep açık

        // Pil simgesi ile büyük sayı arasında dönen zamanlayıcı
        _blink = new System.Windows.Forms.Timer { Interval = 1500 };
        _blink.Tick += (_, _) => { _blinkOn = !_blinkOn; RenderBattery(); };

        _timer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    /// Sağ tık menüsü. Dil değişince baştan kurulur.
    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("Pil durumu", "Battery status"), null, (_, _) => { _popup.SetItems(Items); _popup.ShowAt(Cursor.Position); });
        menu.Items.Add(L.T("Yenile", "Refresh"), null, async (_, _) => await RefreshAsync());
        menu.Items.Add(L.T("Tanılama", "Diagnostics"), null, async (_, _) =>
        {
            // Bağlı tüm HID cihazlarını ve desteklenen markaların cevaplarını log'a yazar, sonra log'u açar.
            // Yeni bir cihaz eklemek ya da bir sorunu çözmek için bu log yeterli.
            await Task.Run(RunDiagnostics);
            OpenLog();
        });
        menu.Items.Add(L.T("Logu aç", "Open log"), null, (_, _) => OpenLog());

        // Dil seçimi
        // Her dilde "Language" yazar; yanlış dilde kalan biri de kolayca bulabilsin
        var lang = new ToolStripMenuItem("Language");
        foreach (var (value, text) in new[] { (L.Lang.Auto, L.T("Otomatik (Windows dili)", "Automatic (Windows language)")),
                                              (L.Lang.Tr, "Türkçe"), (L.Lang.En, "English") })
        {
            var item = new ToolStripMenuItem(text) { Checked = L.Setting == value };
            item.Click += (_, _) => ChangeLanguage(value);
            lang.DropDownItems.Add(item);
        }

        // Windows açılışında otomatik başlatma (Çıkış'ın hemen üstünde)
        var autoStart = new ToolStripMenuItem(L.T("Windows ile başlat", "Start with Windows")) { Checked = AutoStart.IsEnabled };
        autoStart.Click += (_, _) =>
        {
            AutoStart.Set(!autoStart.Checked);
            autoStart.Checked = AutoStart.IsEnabled;
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(lang);
        menu.Items.Add(autoStart);

        // Çıkış her zaman en altta
        menu.Items.Add(L.T("Çıkış", "Exit"), null, (_, _) => ExitThread());
        return menu;
    }

    private static void OpenLog()
    {
        if (!File.Exists(Log.File_)) File.WriteAllText(Log.File_, "");
        Process.Start(new ProcessStartInfo(Log.File_) { UseShellExecute = true });
    }

    private void ChangeLanguage(L.Lang lang)
    {
        L.Set(lang);
        // Eski menü şu an tıklanan menü olduğu için burada dispose edilmiyor
        _tray.ContextMenuStrip = BuildMenu();
        _popup.Invalidate();
        _ = RefreshAsync();   // cihaz durum yazıları yeni dille yeniden üretilsin
    }

    // Düşük pil uyarısı: her cihaz için %10 ve %5 eşiğinde birer kez Windows bildirimi.
    // Cihaz şarja takılınca ya da %15'in üstüne çıkınca uyarılar sıfırlanır.
    private readonly Dictionary<DeviceKind, int> _warnedAt = new();

    private void CheckLowBattery()
    {
        foreach (var d in Items)
        {
            if (d.Charging || d.Percent is null or >= 15)
            {
                if (d.Charging || d.Percent >= 15) _warnedAt.Remove(d.Kind);
                continue;
            }

            int p = d.Percent.Value;
            int level = p < 5 ? 5 : p < 10 ? 10 : 0;
            if (level == 0) continue;
            if (_warnedAt.TryGetValue(d.Kind, out int prev) && prev <= level) continue;

            _warnedAt[d.Kind] = level;
            Log.W($"düşük pil uyarısı: {d.Name} %{p}");
            _tray.ShowBalloonTip(10_000,
                level == 5 ? L.T("Pil kritik seviyede", "Battery critical") : L.T("Pil azaldı", "Battery low"),
                // AirPods değerleri 10'luk adımlarla gelir: yaklaşık olduğunu belirt
                L.T($"{d.Name}: {(d.Kind == DeviceKind.Earbuds ? "~" : "")}%{p} kaldı. Şarja takmayı unutma.",
                    $"{d.Name}: {(d.Kind == DeviceKind.Earbuds ? "~" : "")}{p}% left. Don't forget to charge it."),
                level == 5 ? ToolTipIcon.Error : ToolTipIcon.Warning);
        }
    }

    private DeviceStatus? _airpods;   // sadece yayını görüldüyse listede

    private IReadOnlyList<DeviceStatus> Items =>
        new[] { _headset, _mouse, _keyboard, _asusKeyboard, _airpods }
            .Where(d => d is { Present: true })
            .Select(d => d!)
            .ToList();

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_popup.Visible) { _popup.Hide(); return; }
        // Simgeye tıklayınca panel önce odağı kaybedip kapanır; hemen tekrar açılmasın
        if ((DateTime.Now - _popup.LastHidden).TotalMilliseconds < 300) return;

        _popup.SetItems(Items);
        _popup.ShowAt(Cursor.Position);
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            // Fare ve klavye arka planda okunur, hata olsa bile kulaklığı etkilemez
            var others = Task.Run(() =>
            {
                DeviceStatus m, k, ak;
                try { m = RogMouse.Read(); } catch (Exception ex) { Log.W("fare: " + ex); m = _mouse with { Percent = null, Note = "Okunamadı" }; }
                try { k = Keychron.Read(); } catch (Exception ex) { Log.W("klavye: " + ex); k = _keyboard with { Percent = null, Note = "Okunamadı" }; }
                try { ak = AsusKeyboard.Read(); } catch (Exception ex) { Log.W("ASUS klavye: " + ex); ak = _asusKeyboard with { Percent = null, Note = "Okunamadı" }; }
                return (m, k, ak);
            });

            var info = await Task.Run(() => _device.Read());
            UpdateHeadsetStatus(info);
            UpdateTrayIcon(info);   // simge kulaklık okunur okunmaz güncellensin, diğerlerini bekleme

            (_mouse, _keyboard, _asusKeyboard) = await others;
            try { _airpods = await AirPods.ReadAsync(); } catch (Exception ex) { Log.W("airpods: " + ex.Message); }
            CheckLowBattery();
            if (_popup.Visible) _popup.SetItems(Items);
            UpdateTrayIcon(info);   // şarj olan cihaz bilgisi değişmiş olabilir
        }
        catch (Exception ex)
        {
            Log.W("refresh hatası: " + ex);
            _blink.Stop();
            SetIcon("!", Color.OrangeRed);
            var tip = L.T("Hata: ", "Error: ") + ex.Message;
            _tray.Text = tip.Length > 120 ? tip[..120] : tip;
        }
        finally
        {
            _busy = false;
            // Kulaklık kapalıyken 5 sn'de bir dene ki açılınca hemen görünsün; bağlıyken 30 sn yeterli
            _timer.Interval = _headset.Percent is null ? 5_000 : 30_000;
        }
    }

    private void UpdateTrayIcon(BatteryInfo? info)
    {
        if (info is null)
        {
            _blink.Stop();
            SetIcon("–", Color.Gray);
            var tip = L.T("Kulaklık: ", "Headset: ") + (_device.LastError ?? L.T("bağlı değil", "not connected"));
            _tray.Text = tip.Length > 120 ? tip[..120] : tip;
            return;
        }

        _pct = (int)Math.Round(info.Percent);
        _state = info.ChargeState;
        string state = info.ChargeState switch
        {
            0 => L.T("pilde", "on battery"),
            1 => L.T("şarj oluyor", "charging"),
            2 => L.T("dolu", "full"),
            _ => L.T("durum bilinmiyor", "unknown state")
        };

        // Sadece bir cihaz şarj oluyorsa iki görsel arasında dön
        if (Items.Any(i => i.Charging))
        {
            if (!_blink.Enabled) { _blinkOn = false; _blink.Start(); }
        }
        else
        {
            _blink.Stop();
            _blinkOn = false;
        }
        RenderBattery();
        var t = $"{_headset.Name}: {L.Pct(info.Percent, "0.#")} ({state})";
        _tray.Text = t.Length > 120 ? t[..120] : t;   // en fazla 127 karakter
    }

    private void UpdateHeadsetStatus(BatteryInfo? info)
    {
        if (info is null)
        {
            _headset = _headset with { Percent = null, Charging = false, Present = _device.Found,
                                       Note = _device.LastError ?? L.T("Bağlı değil", "Not connected") };
            return;
        }
        string conn = _device.Path?.Contains("pid_0a96", StringComparison.OrdinalIgnoreCase) == true ? Notes.Cable : Notes.Wireless;
        string note = Notes.Make(conn, charging: info.ChargeState == 1, full: info.ChargeState == 2);
        _headset = _headset with { Name = _device.ProductName ?? _headset.Name, Present = true,
                                   Percent = (int)Math.Round(info.Percent), Charging = info.ChargeState == 1, Note = note };
    }

    // Tepsi simgesi:
    //  - Hiçbir cihaz şarj olmuyorsa: sabit, sadece kulaklığın yüzdesi (tüm simgeyi kaplar)
    //  - Şarj olan cihaz varsa: kulaklık yüzdesi <-> şarj olan cihazın yeşil simgesi
    //    (birden fazla cihaz şarj oluyorsa: yan yana üç yeşil dikey pil)
    // Kulaklık %10'un altındaysa (şarjda değilken) kırmızı.
    // Her şey tam piksele oturacak şekilde, kenar yumuşatmasız çizilir.
    private static readonly Color Green = Color.FromArgb(60, 200, 90);

    private void RenderBattery()
    {
        // Tepsinin gerçek simge boyutunda çiz (100% DPI = 16px, 150% = 24px ...)
        int S = Math.Max(SystemInformation.SmallIconSize.Width, 16);
        bool low = _pct < 10 && _state != 1;
        Color c = low ? Color.FromArgb(255, 72, 72) : Color.White;
        var charging = Items.Where(i => i.Charging).Select(i => i.Kind).ToList();

        using var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.Half;

            if (charging.Count == 0 || !_blinkOn)
                DrawHeadsetWithNumber(g, S, c);
            else if (charging.Count == 1)
                DrawDeviceGlyph(g, S, charging[0], Green);
            else
                DrawThreeBatteries(g, S, Green);
        }
        ApplyBitmap(bmp);
    }

    // 3x5 piksel rakamlar: kulaklığın içine sığacak kadar küçük ama 2x ölçekte çok net
    private static readonly string[][] Glyphs3x5 =
    {
        new[] { "###", "#.#", "#.#", "#.#", "###" }, // 0
        new[] { ".#.", "##.", ".#.", ".#.", "###" }, // 1
        new[] { "###", "..#", "###", "#..", "###" }, // 2
        new[] { "###", "..#", "###", "..#", "###" }, // 3
        new[] { "#.#", "#.#", "###", "..#", "..#" }, // 4
        new[] { "###", "#..", "###", "..#", "###" }, // 5
        new[] { "###", "#..", "###", "#.#", "###" }, // 6
        new[] { "###", "..#", "..#", "..#", "..#" }, // 7
        new[] { "###", "#.#", "###", "#.#", "###" }, // 8
        new[] { "###", "#.#", "###", "..#", "###" }, // 9
    };

    private static void DrawDigits(Graphics g, Brush b, string text, Rectangle area)
    {
        int n = text.Length;
        int unitsW = n * 3 + (n - 1);
        // Yatay ölçek genişliğe göre, dikey ölçek yüksekliğe göre (en fazla 2 katı):
        // rakamlar biraz uzun/dar olur ama simgeyi çok daha iyi doldurur
        int sx = Math.Max(1, area.Width / unitsW);
        int sy = Math.Max(1, Math.Min(area.Height / 5, sx * 2));
        int tw = unitsW * sx, th = 5 * sy;
        int x = area.X + (area.Width - tw + 1) / 2;
        int y = area.Y + (area.Height - th + 1) / 2;
        foreach (char ch in text)
        {
            var gl = Glyphs3x5[ch - '0'];
            for (int r = 0; r < 5; r++)
                for (int col = 0; col < 3; col++)
                    if (gl[r][col] == '#') g.FillRectangle(b, x + col * sx, y + r * sy, sx, sy);
            x += 4 * sx;
        }
    }

    // Kafa bandı + iki kulaklık kabı (cups) ortak çizimi; iç alanı döndürür
    private static Rectangle DrawHeadsetShape(Graphics g, int S, Color c)
    {
        using var b = new SolidBrush(c);
        int cupW = Math.Max(2, S / 8);
        int cupTop = (int)Math.Round(S * 0.45);
        int cupBottom = S - 1;
        int band = Math.Max(2, S / 12);

        // Kulaklık kapları
        g.FillRectangle(b, 0, cupTop, cupW, cupBottom - cupTop + 1);
        g.FillRectangle(b, S - cupW, cupTop, cupW, cupBottom - cupTop + 1);

        // Kafa bandı: yarım elips, kapların ortasından çıkar
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(c, band))
            g.DrawArc(pen, cupW / 2f, band / 2f, S - cupW, 2f * (cupTop - band / 2f) , 180, 180);
        g.SmoothingMode = SmoothingMode.None;

        // Rakamlar için: kapların arası, bandın hemen altından simgenin dibine kadar
        int top = band + Math.Max(1, S / 8);
        return new Rectangle(cupW + 1, top, S - 2 * cupW - 2, S - top);
    }

    // Sadece yüzde: rakamlar simgenin tamamını kaplar, tam piksel ölçekle ve ortalı
    private void DrawHeadsetWithNumber(Graphics g, int S, Color c)
    {
        using var b = new SolidBrush(c);
        string text = _pct.ToString();
        int n = text.Length;
        int unitsW = n * 3 + (n - 1);

        int sx = Math.Max(1, Math.Min(S / unitsW, S / 7));   // tek basamak da iki basamak kalınlığında
        int sy = Math.Max(1, Math.Min(S / 5, sx * 2));
        int tw = unitsW * sx, th = 5 * sy;
        int x = (S - tw) / 2, y = (S - th) / 2;

        foreach (char ch in text)
        {
            var gl = Glyphs3x5[ch - '0'];
            for (int r = 0; r < 5; r++)
                for (int col = 0; col < 3; col++)
                    if (gl[r][col] == '#') g.FillRectangle(b, x + col * sx, y + r * sy, sx, sy);
            x += 4 * sx;
        }
    }

    private static void DrawDeviceGlyph(Graphics g, int S, DeviceKind kind, Color c)
    {
        using var b = new SolidBrush(c);
        int t = Math.Max(1, S / 12);   // çizgi kalınlığı
        switch (kind)
        {
            case DeviceKind.Earbuds:
            {
                // İki AirPods: yuvarlak baş + aşağı inen sap
                int head = Math.Max(3, S * 5 / 16), stemW = Math.Max(1, S / 8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (int cx in new[] { S / 4, S * 3 / 4 })
                {
                    g.FillEllipse(b, cx - head / 2f, 1, head, head);
                    g.FillRectangle(b, cx + (cx < S / 2 ? head / 2 - stemW : -head / 2), head / 2, stemW, S - head / 2 - 1);
                }
                g.SmoothingMode = SmoothingMode.None;
                break;
            }

            case DeviceKind.Headset:
                DrawHeadsetShape(g, S, c);
                break;

            case DeviceKind.Mouse:
            {
                // Dikey fare gövdesi, tuş ayrımı ve tekerlek
                int w = (int)(S * 0.56), h = S - 2;
                var body = new Rectangle((S - w) / 2, 1, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(c, t + 0.5f))
                using (var p = RoundedPath(body, w / 2f))
                    g.DrawPath(pen, p);
                g.SmoothingMode = SmoothingMode.None;
                int midX = S / 2;
                int split = body.Y + (int)(h * 0.42);
                g.FillRectangle(b, body.X + t, split, body.Width - 2 * t, t);       // yatay ayrım
                g.FillRectangle(b, midX - t / 2, body.Y + t, t, split - body.Y - t); // dikey ayrım
                break;
            }

            case DeviceKind.Keyboard:
            {
                // Yatay klavye: çerçeve + tuş noktaları + boşluk tuşu
                int h = (int)(S * 0.62);
                var body = new Rectangle(0, (S - h) / 2, S, h);
                g.FillRectangle(b, body.X, body.Y, body.Width, t);
                g.FillRectangle(b, body.X, body.Bottom - t, body.Width, t);
                g.FillRectangle(b, body.X, body.Y, t, body.Height);
                g.FillRectangle(b, body.Right - t, body.Y, t, body.Height);

                int key = Math.Max(1, S / 12), gap = Math.Max(1, S / 12);
                int cols = (body.Width - 2 * t - gap) / (key + gap);
                int startX = body.X + (body.Width - (cols * (key + gap) - gap)) / 2;
                int row1 = body.Y + t + gap, row2 = row1 + key + gap;
                for (int i = 0; i < cols; i++)
                {
                    g.FillRectangle(b, startX + i * (key + gap), row1, key, key);
                    g.FillRectangle(b, startX + i * (key + gap), row2, key, key);
                }
                int spaceY = row2 + key + gap;
                if (spaceY + key <= body.Bottom - t)
                    g.FillRectangle(b, body.X + body.Width / 4, spaceY, body.Width / 2, key);
                break;
            }
        }
    }

    private static void DrawThreeBatteries(Graphics g, int S, Color c)
    {
        using var b = new SolidBrush(c);
        int gap = Math.Max(1, S / 12);
        int w = (S - 2 * gap) / 3;
        int nubH = Math.Max(1, S / 12), nubW = Math.Max(1, w / 2);
        int top = 1 + nubH, h = S - 2 - nubH;
        int x0 = (S - (3 * w + 2 * gap)) / 2;
        for (int i = 0; i < 3; i++)
        {
            int x = x0 + i * (w + gap);
            g.FillRectangle(b, x + (w - nubW) / 2, top - nubH, nubW, nubH); // uç
            g.FillRectangle(b, x, top, w, h);                               // gövde
        }
    }

    private static GraphicsPath RoundedPath(Rectangle r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Okunaklı rakamlar için Bahnschrift (normal genişlik) (Windows 10/11'de var), yoksa Segoe UI
    private static FontFamily? _family;
    private static FontFamily PickFont()
    {
        if (_family != null) return _family;
        foreach (var name in new[] { "Bahnschrift", "Segoe UI" })
        {
            var f = FontFamily.Families.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (f != null) return _family = f;
        }
        return _family = FontFamily.GenericSansSerif;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void ApplyBitmap(Bitmap bmp)
    {
        IntPtr h = bmp.GetHicon();
        using (var tmp = Icon.FromHandle(h))
        {
            var icon = (Icon)tmp.Clone();
            _tray.Icon = icon;
            _currentIcon?.Dispose();
            _currentIcon = icon;
        }
        DestroyIcon(h);
    }

    private void SetIcon(string text, Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float size = text.Length >= 3 ? 15f : 21f;
            using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, brush, new RectangleF(0, 0, 32, 32), fmt);
        }
        ApplyBitmap(bmp);
    }

    /// Tanılama: tüm HID cihazlarının dökümü + markaya özel denemeler (şimdilik ASUS).
    private static void RunDiagnostics()
    {
        Log.W("========== Tanılama başladı ==========");
        Log.W($"BatteryTray {typeof(TrayApp).Assembly.GetName().Version}, Windows {Environment.OSVersion.Version}");
        try
        {
            foreach (var g in DeviceList.Local.GetHidDevices().GroupBy(d => (d.VendorID, d.ProductID)).OrderBy(g => g.Key.VendorID))
            {
                var first = g.First();
                string mfr = "?", name = "?";
                try { mfr = first.GetManufacturer(); } catch { }
                try { name = first.GetProductName(); } catch { }
                Log.W($"[HID] VID=0x{g.Key.VendorID:X4} PID=0x{g.Key.ProductID:X4} \"{mfr}\" \"{name}\" ({g.Count()} arayüz)");
                foreach (var d in g)
                {
                    string usages = "?";
                    try
                    {
                        usages = string.Join(",", d.GetReportDescriptor().DeviceItems.SelectMany(i => i.Usages.GetAllValues())
                            .Select(u => "0x" + (u >> 16).ToString("X4")).Distinct());
                    }
                    catch { }
                    Log.W($"        pages={usages} in={d.GetMaxInputReportLength()} out={d.GetMaxOutputReportLength()} " +
                          $"feat={d.GetMaxFeatureReportLength()} {d.DevicePath}");
                }
            }
        }
        catch (Exception ex) { Log.W("[HID] döküm hatası: " + ex.Message); }

        try { AsusKeyboard.Diagnose(); } catch (Exception ex) { Log.W("[ASUS tanı] " + ex); }
        Log.W("========== Tanılama bitti ==========");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _popup.Dispose();
            AirPods.Stop();
            _blink.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _currentIcon?.Dispose();
            _device.Dispose();
        }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}

/// Windows açılışında başlatma: HKCU\...\Run anahtarı (yönetici izni gerekmez)
internal static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "BatteryTray";
    private const string OldName = "HS80Tray";   // eski sürümün kaydı

    public static bool IsEnabled
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool enable)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, writable: true)
                      ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
        if (enable) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, throwOnMissingValue: false);
        k.DeleteValue(OldName, throwOnMissingValue: false);   // HS80Tray adıyla kalan eski kaydı temizle
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "BatteryTray_SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
