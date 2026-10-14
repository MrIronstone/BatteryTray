using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BatteryTray;

/// Tepsi simgesine tıklanınca açılan, Windows 11 tarzı koyu küçük panel.
/// Her cihaz için: simge, ad, durum notu, yüzde ve küçük pil çubuğu.
internal sealed class PopupForm : Form
{
    private IReadOnlyList<DeviceStatus> _items = Array.Empty<DeviceStatus>();
    private IReadOnlyList<DeviceStatus> _nearby = Array.Empty<DeviceStatus>();
    private int _tab;   // 0 = Cihazlarım, 1 = Yakındakiler

    /// "Yakındakiler" sekmesinin verisi (açıkken 2 sn'de bir yenilenir)
    public Func<IReadOnlyList<DeviceStatus>>? NearbyProvider { get; set; }
    private readonly System.Windows.Forms.Timer _nearTimer = new() { Interval = 2000 };
    public DateTime LastHidden { get; private set; }

    private const int W = 320, Header = 52, RowH = 62, Footer = 14;

    private static readonly Color Bg = Color.FromArgb(32, 32, 32);
    private static readonly Color RowBg = Color.FromArgb(43, 43, 43);
    private static readonly Color Text1 = Color.FromArgb(245, 245, 245);
    private static readonly Color Text2 = Color.FromArgb(160, 160, 160);
    private static readonly Color Track = Color.FromArgb(70, 70, 70);

    private readonly string _iconFont;

    public PopupForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        DoubleBuffered = true;
        KeyPreview = true;
        Width = W;
        Height = Header + RowH * 3 + Footer;

        _iconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons")
            ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        Deactivate += (_, _) => HidePopup();
        MouseClick += OnClick;
        _nearTimer.Tick += (_, _) => { if (Visible && _tab == 1) RefreshNearby(); };
        _nearTimer.Start();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HidePopup(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            cp.ExStyle |= 0x00000080;      // WS_EX_TOOLWINDOW (Alt+Tab'da görünmesin)
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11: yuvarlak köşe + koyu tema
        try
        {
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }
        catch { /* Windows 10'da bu öznitelikler yok, sorun değil */ }
    }

    public void SetItems(IReadOnlyList<DeviceStatus> items)
    {
        _items = items;
        Relayout();
    }

    private void RefreshNearby()
    {
        _nearby = NearbyProvider?.Invoke() ?? Array.Empty<DeviceStatus>();
        Relayout();
    }

    private IReadOnlyList<DeviceStatus> Rows => _tab == 0 ? _items : _nearby;

    /// Satır sayısına göre yüksekliği ayarla; panel açıksa alt kenarı sabit tut
    private void Relayout()
    {
        int h = Header + RowH * Math.Max(1, Rows.Count) + Footer;
        if (h != Height)
        {
            int bottom = Bottom;
            Height = h;
            if (Visible) Top = bottom - h;
        }
        Invalidate();
    }

    private void OnClick(object? sender, MouseEventArgs e)
    {
        if (e.Y > Header - 6) return;
        int tab = e.X < Width / 2 ? 0 : 1;
        if (tab == _tab) return;
        _tab = tab;
        if (_tab == 1) RefreshNearby(); else Relayout();
    }

    /// Paneli tıklanan noktanın (tepsi simgesinin) tam üstüne ortalar.
    public void ShowAt(Point anchor)
    {
        var wa = Screen.FromPoint(anchor).WorkingArea;
        int x = anchor.X - Width / 2;
        x = Math.Clamp(x, wa.Left + 8, wa.Right - Width - 8);
        int y = wa.Bottom - Height - 12;   // görev çubuğu altta varsayımı
        if (anchor.Y < wa.Top)              // görev çubuğu üstteyse
            y = wa.Top + 12;
        Location = new Point(x, y);
        Show();
        Activate();
    }

    private void HidePopup()
    {
        if (!Visible) return;
        Hide();
        LastHidden = DateTime.Now;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using var titleFont = new Font("Segoe UI Semibold", 11f);
        using var nameFont = new Font("Segoe UI Semibold", 10f);
        using var noteFont = new Font("Segoe UI", 8.5f);
        using var pctFont = new Font("Segoe UI Semibold", 13f);
        using var iconFont = new Font(_iconFont, 18f, GraphicsUnit.Pixel);
        using var t1 = new SolidBrush(Text1);
        using var t2 = new SolidBrush(Text2);

        // Sekmeler
        using (var tabFont = new Font("Segoe UI Semibold", 10f))
        {
            string[] tabs = { "Cihazlarım", "Yakındakiler" };
            for (int i = 0; i < 2; i++)
            {
                var r = new Rectangle(i * Width / 2, 10, Width / 2, 28);
                TextRenderer.DrawText(g, tabs[i], tabFont, r, i == _tab ? Text1 : Text2,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (i == _tab)
                    using (var ub = new SolidBrush(Color.FromArgb(60, 200, 90)))
                    using (var up = Rounded(new Rectangle(r.X + r.Width / 2 - 24, r.Bottom + 2, 48, 3), 1))
                        g.FillPath(ub, up);
            }
        }

        int y = Header;
        if (Rows.Count == 0)
        {
            TextRenderer.DrawText(g, _tab == 1 ? "Yakında Bluetooth cihazı bulunamadı" : "Cihaz yok", noteFont,
                new Rectangle(10, y, Width - 20, RowH - 8), Text2,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        foreach (var it in Rows)
        {
            var row = new Rectangle(10, y, Width - 20, RowH - 8);
            using (var rb = new SolidBrush(RowBg))
            using (var rp = Rounded(row, 8))
                g.FillPath(rb, rp);

            // Cihaz simgesi (Segoe Fluent / MDL2 glifleri)
            string glyph = it.Kind switch
            {
                DeviceKind.Headset or DeviceKind.Earbuds => "",
                DeviceKind.Other => "\uE702",   // Bluetooth
                DeviceKind.Mouse => "",
                _ => "",
            };
            var iconRect = new Rectangle(row.X + 10, row.Y, 32, row.Height);
            TextRenderer.DrawText(g, glyph, iconFont, iconRect, Text1,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Ad ve not
            int tx = row.X + 50;
            int textW = row.Width - 50 - 90;
            TextRenderer.DrawText(g, it.Name, nameFont, new Rectangle(tx, row.Y + 8, textW, 20), Text1,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            string note = it.Note;   // biçim Notes.Make ile tek yerden belirleniyor
            TextRenderer.DrawText(g, note, noteFont, new Rectangle(tx, row.Y + 30, textW, 18), Text2,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

            // Sağ taraf: yüzde + çubuk
            int rx = row.Right - 80;
            if (it.Percent is int p)
            {
                Color c = it.Charging ? Color.FromArgb(60, 200, 90)
                        : p < 10 ? Color.FromArgb(240, 70, 70)
                        : p < 25 ? Color.FromArgb(240, 190, 40)
                        : Color.FromArgb(60, 200, 90);

                string pt = (it.Charging ? "⚡" : "") + $"%{p}";
                TextRenderer.DrawText(g, pt, pctFont, new Rectangle(rx, row.Y + 6, 70, 26), Text1,
                    TextFormatFlags.Right | TextFormatFlags.NoPadding);

                var bar = new Rectangle(rx + 10, row.Y + 36, 60, 6);
                using (var tb = new SolidBrush(Track))
                using (var tp = Rounded(bar, 3))
                    g.FillPath(tb, tp);
                int fw = Math.Max(6, bar.Width * p / 100);
                using (var fb = new SolidBrush(c))
                using (var fp = Rounded(new Rectangle(bar.X, bar.Y, fw, bar.Height), 3))
                    g.FillPath(fb, fp);
            }
            else if (it.Wired)
            {
                // Kablolu mod: yüzde yerine yatay, yeşil priz simgesi
                DrawPlug(g, row.Right - 14, row.Y + row.Height / 2, Color.FromArgb(60, 200, 90));
            }
            else
            {
                TextRenderer.DrawText(g, "—", pctFont, new Rectangle(rx, row.Y + 14, 70, 26), Text2,
                    TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }

            y += RowH;
        }
    }

    /// Yatay fiş: solda kablo, ortada gövde, sağda iki uç. (right, cy) = sağ kenar ve dikey orta.
    private static void DrawPlug(Graphics g, int right, int cy, Color c)
    {
        using var b = new SolidBrush(c);
        using var pen = new Pen(c, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        int prongLen = 8, bodyW = 16, bodyH = 18, cableLen = 14;
        int bodyRight = right - prongLen;
        var body = new Rectangle(bodyRight - bodyW, cy - bodyH / 2, bodyW, bodyH);

        // Uçlar
        g.FillRectangle(b, bodyRight, cy - 6, prongLen, 3);
        g.FillRectangle(b, bodyRight, cy + 3, prongLen, 3);
        // Gövde (sol tarafı yuvarlak)
        using (var gp = Rounded(body, 5)) g.FillPath(b, gp);
        // Kablo
        g.DrawLine(pen, body.X - cableLen, cy, body.X, cy);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        if (r.Width < d) d = r.Width;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
