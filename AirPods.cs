using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace BatteryTray;

// ---------------------------------------------------------------------------
// AirPods pil bilgisi: Bluetooth LE yayınlarından ("proximity pairing" mesajı).
// Apple üretici verisi (company id 0x004C), ilk bayt 0x07 ise:
//   [3..4] model kimliği (little-endian)
//   [5]    durum (bit 5: hangi kulaklık "birincil" -> sol/sağ yer değiştirir)
//   [6]    üst/alt 4 bit = iki kulaklığın pili (0..10 => %0..%100, 15 = yok)
//   [7]    üst 4 bit = şarj bayrakları (bit0/bit1 kulaklıklar, bit2 kutu), alt 4 bit = kutu pili
// Değerler 10'ar puanlık. Kulaklıklar takılıyken ya da kutu açıkken yayın yapılır.
// Yakındaki başka Apple cihazlarını elemek için sadece güçlü sinyaller (RSSI) alınır.
// Ayrıca satır SADECE bu bilgisayarla eşleşmiş bir AirPods varsa gösterilir; yüzde de
// yalnızca o AirPods şu an bilgisayara bağlıyken güncellenir.
// ---------------------------------------------------------------------------
internal static class AirPods
{
    private const short MinRssi = -65;   // ~birkaç metre; komşunun AirPods'u karışmasın

    private static BluetoothLEAdvertisementWatcher? _watcher;
    private static readonly object _lock = new();
    private static (int? left, int? right, int? box, bool chL, bool chR, bool chBox, DateTime at)? _last;
    private static readonly HashSet<string> _loggedRaw = new();
    private static string? _startError;

    // Yakındaki TÜM AirPods/Beats yayınları (eşleşmemiş olanlar dahil), adrese göre
    private sealed record Ad(string Model, short Rssi, int? L, int? R, int? Box, bool ChL, bool ChR, bool ChBox, DateTime At);
    private static readonly Dictionary<ulong, Ad> _nearby = new();

    // Diğer tüm Bluetooth LE cihazları: ad, üretici, sinyal ve (varsa) Battery Service yüzdesi
    private sealed record Ble(string? Name, string? Maker, short Rssi, int? Battery, DateTime At);
    private static readonly Dictionary<ulong, Ble> _ble = new();

    private static string? MakerName(ushort id) => id switch
    {
        0x004C => "Apple", 0x0006 => "Microsoft", 0x0075 => "Samsung", 0x00E0 => "Google",
        0x0087 => "Garmin", 0x038F => "Xiaomi", 0x027D => "Huawei", 0x0157 => "Huami / Mi Band",
        0x0171 => "Amazon", 0x00D2 => "Bose", 0x012D => "Sony", 0x0310 => "JBL / Harman",
        0x02E5 => "Espressif", 0x0059 => "Nordic", 0x0499 => "Ruuvi", 0x0822 => "Anker / Soundcore",
        _ => null
    };

    private static void RecordBle(BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var adv = args.Advertisement;
        string? name = string.IsNullOrWhiteSpace(adv.LocalName) ? null : adv.LocalName.Trim();
        string? maker = adv.ManufacturerData.Select(m => MakerName(m.CompanyId)).FirstOrDefault(n => n != null);

        // Standart Battery Service (0x180F) hizmet verisi: [0F 18 yüzde]
        int? battery = null;
        foreach (var sec in adv.DataSections.Where(x => x.DataType == 0x16))
        {
            var b = new byte[sec.Data.Length];
            DataReader.FromBuffer(sec.Data).ReadBytes(b);
            if (b.Length >= 3 && b[0] == 0x0F && b[1] == 0x18 && b[2] <= 100) battery = b[2];
        }

        lock (_ble)
        {
            _ble.TryGetValue(args.BluetoothAddress, out var old);
            // Ad ve pil bazen sadece bazı paketlerde gelir: eskisini koru
            _ble[args.BluetoothAddress] = new Ble(name ?? old?.Name, maker ?? old?.Maker,
                args.RawSignalStrengthInDBm, battery ?? old?.Battery, DateTime.Now);
        }
    }

    private static string Distance(short rssi) =>
        rssi >= -55 ? "çok yakın" : rssi >= -70 ? "yakın" : rssi >= -85 ? "orta" : "uzak";

    // Bilinen model kimlikleri (proximity pairing [3..4]). Bilinmeyenler kodla gösterilir.
    private static string ModelName(string code) => code switch
    {
        "2002" => "AirPods (1. nesil)",
        "200F" => "AirPods (2. nesil)",
        "2013" => "AirPods (3. nesil)",
        "2019" or "201B" => "AirPods 4",
        "200E" => "AirPods Pro",
        "2014" or "2024" => "AirPods Pro 2",
        "2027" => "AirPods Pro 3",
        "200A" or "201F" => "AirPods Max",
        "2003" or "200B" or "2011" or "2010" or "2016" or "2012" or "2017" or "2006" or "2009" or "2005" => "Beats",
        _ => $"Apple kulaklık ({code})"
    };

    public static void Start()
    {
        if (_watcher != null) return;
        try
        {
            // Aktif tarama: cihazlar adlarını genelde sadece "scan response" ile gönderir
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.Received += OnReceived;
            _watcher.Stopped += (_, e) => Log.W($"[AirPods] tarama durdu: {e.Error}");
            _watcher.Start();
            Log.W("[AirPods] Bluetooth LE taraması başladı");
        }
        catch (Exception ex)
        {
            _startError = ex.Message;
            Log.W("[AirPods] tarama başlatılamadı: " + ex.Message);
        }
    }

    private static void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            RecordBle(args);
            foreach (var md in args.Advertisement.ManufacturerData)
            {
                if (md.CompanyId != 0x004C) continue;
                var d = new byte[md.Data.Length];
                DataReader.FromBuffer(md.Data).ReadBytes(d);
                if (d.Length < 9 || d[0] != 0x07) continue;   // sadece proximity pairing mesajı

                // Ham veriyi, her farklı model/biçim için bir kez logla (Pro 3'ü tanımak için)
                string key = $"{d[4]:X2}{d[3]:X2}";   // model kimliği little-endian (ör. 0E 20 -> 200E = AirPods Pro)
                lock (_loggedRaw)
                    if (_loggedRaw.Add(key))
                        Log.W($"[AirPods] yayın: model=0x{key} rssi={args.RawSignalStrengthInDBm} ham={BitConverter.ToString(d)}");

                bool flip = (d[5] & 0x20) == 0;
                int a = d[6] >> 4, b = d[6] & 0x0F;
                int leftRaw = flip ? a : b, rightRaw = flip ? b : a;
                int caseRaw = d[7] & 0x0F;
                int chg = d[7] >> 4;

                static int? Pct(int v) => v <= 10 ? v * 10 : null;   // 15 = bilinmiyor/takılı değil
                var left = Pct(leftRaw);
                var right = Pct(rightRaw);
                var box = Pct(caseRaw);
                bool chL = (chg & (flip ? 0b010 : 0b001)) != 0;
                bool chR = (chg & (flip ? 0b001 : 0b010)) != 0;
                bool chBox = (chg & 0b100) != 0;

                if (left is null && right is null && box is null) continue;

                lock (_nearby)
                    _nearby[args.BluetoothAddress] = new Ad(ModelName(key), args.RawSignalStrengthInDBm,
                        left, right, box, chL, chR, chBox, DateTime.Now);

                // "Benim AirPods'um" için sadece güçlü (yakın) sinyaller
                if (args.RawSignalStrengthInDBm < MinRssi) continue;
                lock (_lock) _last = (left, right, box, chL, chR, chBox, DateTime.Now);
            }
        }
        catch (Exception ex) { Log.W("[AirPods] yayın okunamadı: " + ex.Message); }
    }

    // Eşleşmiş AirPods: adı ve bağlı olup olmadığı (30 sn'de bir yenilenir)
    private static (string name, bool connected)? _paired;
    private static DateTime _pairedCheckedAt = DateTime.MinValue;
    private static bool _pairedLogged;

    private static Task? _pairedTask;

    private static async Task RefreshPairedAsync()
    {
        if ((DateTime.Now - _pairedCheckedAt).TotalSeconds < 60) return;
        _pairedCheckedAt = DateTime.Now;
        try
        {
            // Eşleşmiş Bluetooth cihazları (AssociationEndpoint) + "bağlı mı" özelliği
            const string IsConnected = "System.Devices.Aep.IsConnected";
            var list = await DeviceInformation.FindAllAsync(
                "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True",
                new[] { IsConnected }, DeviceInformationKind.AssociationEndpoint);
            (string, bool)? found = null;
            foreach (var di in list.Where(d => d.Name.Contains("AirPods", StringComparison.OrdinalIgnoreCase)))
            {
                bool connected = di.Properties.TryGetValue(IsConnected, out var v) && v is bool b && b;
                found = (di.Name, connected);
                if (connected) break;   // birden fazla eşleşmiş AirPods varsa bağlı olanı tercih et
            }
            _paired = found;
            if (!_pairedLogged || found is null)
            {
                _pairedLogged = true;
                Log.W(found is null ? "[AirPods] eşleşmiş AirPods yok"
                                    : $"[AirPods] eşleşmiş: {found.Value.Item1}, bağlı={found.Value.Item2}");
            }
        }
        catch (Exception ex) { Log.W("[AirPods] eşleşmiş cihazlar okunamadı: " + ex.Message); }
    }

    /// Panel satırı. Eşleşmiş AirPods yoksa null (satır gösterilmez).
    public static async Task<DeviceStatus?> ReadAsync()
    {
        // Eşleşmiş cihaz sorgusu Windows'ta 30 sn'ye kadar sürebiliyor: arka planda çalışsın,
        // panel/simge onu beklemesin (ilk turda satır henüz görünmeyebilir)
        if (_pairedTask is null || _pairedTask.IsCompleted) _pairedTask = Task.Run(RefreshPairedAsync);
        await Task.CompletedTask;
        if (_paired is null) return null;
        var (pairedName, connected) = _paired.Value;

        Start();
        (int? left, int? right, int? box, bool chL, bool chR, bool chBox, DateTime at)? v;
        lock (_lock) v = _last;

        if (_startError is not null)
            return new(DeviceKind.Earbuds, pairedName, null, false, "Bluetooth taranamıyor");
        if (v is null)
            return new(DeviceKind.Earbuds, pairedName, null, false,
                connected ? "Pil bilgisi bekleniyor" : "Bağlı değil");

        var x = v.Value;
        // Ana yüzde: takılı kulaklıkların en düşüğü (sol/sağ farklı olabilir)
        int? main = (x.left, x.right) switch
        {
            (int l, int r) => Math.Min(l, r),
            (int l, null) => l,
            (null, int r) => r,
            _ => x.box
        };

        var parts = new List<string>();
        if (x.left is int l2) parts.Add($"Sol %{l2}");
        if (x.right is int r2) parts.Add($"Sağ %{r2}");
        if (x.box is int b2) parts.Add($"Kutu %{b2}");
        string detail = string.Join(" · ", parts);

        bool stale = !connected || (DateTime.Now - x.at).TotalSeconds > 90;
        if (stale)
            return new(DeviceKind.Earbuds, pairedName, main, false,
                (connected ? "Kutuda" : "Bağlı değil") + $" · son {x.at:HH:mm}");

        bool charging = x.chL || x.chR;
        string note = (charging ? "Şarj oluyor · " : "") + detail;
        return new(DeviceKind.Earbuds, pairedName, main, charging, note);
    }

    /// "Yakındakiler" sekmesi: son 60 sn'de görülen cihazlar, en yakından uzağa.
    /// AirPods/Beats pil bilgisiyle; diğerleri adı (ya da üreticisi) ve mesafesiyle.
    public static IReadOnlyList<DeviceStatus> Nearby()
    {
        const int MaxRows = 8;
        List<(ulong addr, Ad ad)> ads;
        lock (_nearby)
        {
            foreach (var k in _nearby.Where(kv => (DateTime.Now - kv.Value.At).TotalSeconds > 60).Select(kv => kv.Key).ToList())
                _nearby.Remove(k);
            ads = _nearby.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        var earbuds = ads.Select(x => (rssi: x.ad.Rssi, status: EarbudStatus(x.ad))).ToList();
        var skip = ads.Select(x => x.addr).ToHashSet();

        List<(short rssi, DeviceStatus status)> others;
        lock (_ble)
        {
            foreach (var k in _ble.Where(kv => (DateTime.Now - kv.Value.At).TotalSeconds > 60).Select(kv => kv.Key).ToList())
                _ble.Remove(k);
            others = _ble.Where(kv => !skip.Contains(kv.Key) && (kv.Value.Name != null || kv.Value.Battery != null))
                .Select(kv =>
                {
                    var b = kv.Value;
                    string title = b.Name ?? (b.Maker != null ? $"{b.Maker} cihazı" : "İsimsiz cihaz");
                    string note = Distance(b.Rssi) + (b.Name != null && b.Maker != null ? $" · {b.Maker}" : "");
                    return (b.Rssi, new DeviceStatus(DeviceKind.Other, title, b.Battery, false, note));
                }).ToList();
        }

        return earbuds.Concat(others)
            .OrderByDescending(x => x.Item1)
            .Take(MaxRows)
            .Select(x => x.Item2)
            .ToList();
    }

    private static DeviceStatus EarbudStatus(Ad a)
    {
        int? main = (a.L, a.R) switch
        {
            (int l, int r) => Math.Min(l, r),
            (int l, null) => l,
            (null, int r) => r,
            _ => a.Box
        };
        // Kaba mesafe tahmini (RSSI'den; duvarlar ve vücut çok etkiler)
        var parts = new List<string> { Distance(a.Rssi) };
        if (a.L is int l2) parts.Add($"Sol %{l2}");
        if (a.R is int r2) parts.Add($"Sağ %{r2}");
        if (a.Box is int b2) parts.Add($"Kutu %{b2}");
        return new DeviceStatus(DeviceKind.Earbuds, a.Model, main, a.ChL || a.ChR, string.Join(" · ", parts));
    }

    public static void Stop()
    {
        try { _watcher?.Stop(); } catch { }
    }
}
