// ClaudeTray: shows Claude usage limits (5-hour session, weekly, weekly model-specific)
// as a small transparent overlay on the Windows 11 taskbar, right after the Widgets/weather button.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

class Limit
{
    public string Label;
    public string Name;
    public double? Pct;
    public DateTime? Reset;
}

class TrayForm : Form
{
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const int PollMinutes = 3;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "ClaudeTray";

    readonly System.Windows.Forms.Timer layoutTimer = new System.Windows.Forms.Timer();
    readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
    readonly ToolTip tip = new ToolTip();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    ToolStripMenuItem startupItem;

    List<Limit> limits = new List<Limit>();
    string status = "Loading...";
    DateTime? lastOk;
    int fetching;

    AutomationElement widgetsButton;
    DateTime lastWidgetSearch = DateTime.MinValue;
    Rectangle lastBounds = Rectangle.Empty;
    float lastScale = 0;
    bool lastLight;
    DateTime lastRender = DateTime.MinValue;

    public TrayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = AppName;

        limits.Add(new Limit { Label = "5h", Name = "Current session (5 hours)" });
        limits.Add(new Limit { Label = "Wk", Name = "Current week (all models)" });
        limits.Add(new Limit { Label = "Fable", Name = "Current week (Fable)" });

        menu.Items.Add("Refresh now", null, delegate { Fetch(); });
        startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
        startupItem.Checked = IsStartupEnabled();
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { Close(); });
        ContextMenuStrip = menu;

        tip.InitialDelay = 300;
        tip.AutoPopDelay = 30000;
        tip.ShowAlways = true;

        layoutTimer.Interval = 1500;
        layoutTimer.Tick += delegate { Relayout(false); };
        pollTimer.Interval = PollMinutes * 60 * 1000;
        pollTimer.Tick += delegate { Fetch(); };

        MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Fetch(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00080000 /*WS_EX_LAYERED*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/
                        | 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000008 /*WS_EX_TOPMOST*/;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Relayout(true);
        layoutTimer.Start();
        pollTimer.Start();
        Fetch();
    }

    // ---------------------------------------------------------------- data

    void Fetch()
    {
        if (Interlocked.Exchange(ref fetching, 1) == 1) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            string err = null;
            List<Limit> result = null;
            try { result = DoFetch(); }
            catch (Exception ex) { err = ex.Message; }
            finally { Interlocked.Exchange(ref fetching, 0); }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (result != null) { limits = result; lastOk = DateTime.Now; status = null; }
                    else status = err;
                    Relayout(true);
                });
            }
            catch { }
        });
    }

    static string CredentialsPath()
    {
        string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, ".credentials.json");
    }

    List<Limit> DoFetch()
    {
        string path = CredentialsPath();
        if (!File.Exists(path)) throw new Exception("No Claude Code login found (" + path + "). Run 'claude' and log in.");

        var js = new JavaScriptSerializer();
        var creds = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
        var oauth = creds.ContainsKey("claudeAiOauth") ? creds["claudeAiOauth"] as Dictionary<string, object> : null;
        if (oauth == null || !oauth.ContainsKey("accessToken")) throw new Exception("No claude.ai OAuth token in credentials file.");
        string token = Convert.ToString(oauth["accessToken"]);
        if (oauth.ContainsKey("expiresAt") && oauth["expiresAt"] != null)
        {
            long exp = Convert.ToInt64(oauth["expiresAt"]);
            if (DateTimeOffset.FromUnixTimeMilliseconds(exp) < DateTimeOffset.UtcNow)
                throw new Exception("Claude login token expired. Open Claude Code once to refresh it.");
        }

        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
        var req = (HttpWebRequest)WebRequest.Create(UsageUrl);
        req.Method = "GET";
        req.Timeout = 20000;
        req.Accept = "application/json";
        req.UserAgent = "claude-tray/1.0";
        req.Headers["Authorization"] = "Bearer " + token;
        req.Headers["anthropic-beta"] = "oauth-2025-04-20";

        string body;
        try
        {
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                body = sr.ReadToEnd();
        }
        catch (WebException wex)
        {
            var hr = wex.Response as HttpWebResponse;
            if (hr != null)
            {
                int code = (int)hr.StatusCode;
                if (code == 401) throw new Exception("Unauthorized (401). Open Claude Code to refresh your login.");
                if (code == 429) throw new Exception("Rate limited (429). Will retry later.");
                throw new Exception("HTTP " + code + " from usage API.");
            }
            throw;
        }

        try
        {
            string dumpDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
            Directory.CreateDirectory(dumpDir);
            File.WriteAllText(Path.Combine(dumpDir, "last-usage.json"), body);
        }
        catch { }

        var data = js.Deserialize<Dictionary<string, object>>(body);
        var list = new List<Limit>();
        list.Add(Parse(data, "five_hour", "5h", "Current session (5 hours)"));
        list.Add(Parse(data, "seven_day", "Wk", "Current week (all models)"));

        list.Add(FindFable(data, js));
        return list;
    }

    // Third limit: the weekly Fable limit. The API has no fixed key for it, so look in every
    // place a model-scoped limit can appear and match "fable" case-insensitively.
    static Limit FindFable(Dictionary<string, object> data, JavaScriptSerializer js)
    {
        const string Label = "Fable", Name = "Current week (Fable)";

        // 1. Top-level bucket such as "seven_day_fable".
        foreach (var kv in data)
        {
            if (kv.Key.IndexOf("fable", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var l = Parse(data, kv.Key, Label, Name);
            if (l.Pct.HasValue) return l;
        }

        // 2. Entry in the "limits" array whose kind/group/scope mentions Fable.
        object lo;
        var arr = data.TryGetValue("limits", out lo) ? lo as System.Collections.ArrayList : null;
        if (arr != null)
        {
            foreach (var item in arr)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                string text = js.Serialize(new object[] { Get(d, "kind"), Get(d, "group"), Get(d, "scope") });
                if (text.IndexOf("fable", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var l = new Limit { Label = Label, Name = Name };
                object p = Get(d, "percent") ?? Get(d, "utilization");
                if (p != null) l.Pct = Convert.ToDouble(p, CultureInfo.InvariantCulture);
                object r = Get(d, "resets_at");
                DateTimeOffset dto;
                if (r != null && DateTimeOffset.TryParse(Convert.ToString(r, CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                    l.Reset = dto.LocalDateTime;
                if (l.Pct.HasValue) return l;
            }
        }

        // 3. Generic scoped weekly shares, if present.
        object sharesObj;
        if (data.TryGetValue("weekly_scoped_shares", out sharesObj) && sharesObj != null)
        {
            string text = js.Serialize(sharesObj);
            if (text.IndexOf("fable", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var asDict = sharesObj as Dictionary<string, object>;
                if (asDict != null)
                    foreach (var kv in asDict)
                        if (kv.Key.IndexOf("fable", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var l = Parse(asDict, kv.Key, Label, Name);
                            if (l.Pct.HasValue) return l;
                        }
            }
        }

        return new Limit { Label = Label, Name = "Current week (Fable): no usage reported yet" };
    }

    static object Get(Dictionary<string, object> d, string key)
    {
        object v;
        return d.TryGetValue(key, out v) ? v : null;
    }

    static Limit Parse(Dictionary<string, object> data, string key, string label, string name)
    {
        var l = new Limit { Label = label, Name = name };
        object o;
        if (!data.TryGetValue(key, out o)) return l;
        var d = o as Dictionary<string, object>;
        if (d == null) return l;
        if (d.ContainsKey("utilization") && d["utilization"] != null)
            l.Pct = Convert.ToDouble(d["utilization"], CultureInfo.InvariantCulture);
        if (d.ContainsKey("resets_at") && d["resets_at"] != null)
        {
            DateTimeOffset dto;
            if (DateTimeOffset.TryParse(Convert.ToString(d["resets_at"], CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal, out dto))
                l.Reset = dto.LocalDateTime;
        }
        return l;
    }

    // ---------------------------------------------------------------- startup

    static bool IsStartupEnabled()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
            return k != null && k.GetValue(AppName) != null;
    }

    void ToggleStartup()
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (k.GetValue(AppName) != null) k.DeleteValue(AppName, false);
            else k.SetValue(AppName, "\"" + Application.ExecutablePath + "\"");
        }
        startupItem.Checked = IsStartupEnabled();
    }

    // ---------------------------------------------------------------- placement

    static Rectangle? ToRect(AutomationElement el)
    {
        var r = el.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width <= 0) return null;
        return new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    }

    Rectangle? WidgetsRect(IntPtr taskbar)
    {
        try { if (widgetsButton != null) { var r = ToRect(widgetsButton); if (r.HasValue) return r; } }
        catch { widgetsButton = null; }

        if ((DateTime.Now - lastWidgetSearch).TotalSeconds < 10) return null;
        lastWidgetSearch = DateTime.Now;
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            widgetsButton = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "WidgetsButton"));
            if (widgetsButton != null) return ToRect(widgetsButton);
        }
        catch { widgetsButton = null; }
        return null;
    }

    static bool ForegroundIsFullscreen(Rectangle screen)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var cls = new StringBuilder(256);
        Native.GetClassName(fg, cls, cls.Capacity);
        string c = cls.ToString();
        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
        Native.RECT r;
        if (!Native.GetWindowRect(fg, out r)) return false;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }

    void Relayout(bool forceRedraw)
    {
        IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero) { if (Visible) Hide(); return; }
        Native.RECT tr;
        Native.GetWindowRect(tb, out tr);
        var tbRect = Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom);
        var screen = Screen.FromRectangle(tbRect).Bounds;

        bool tbHidden = tbRect.Height < 8 || tbRect.Top >= screen.Bottom - 4;
        if (tbHidden || ForegroundIsFullscreen(screen))
        {
            if (Visible) Hide();
            return;
        }

        float scale = Native.DpiFor(tb) / 96f;
        int h = tbRect.Height;
        int gap = (int)(6 * scale);
        int x;
        var w = WidgetsRect(tb);
        if (w.HasValue && w.Value.Right > tbRect.Left && w.Value.Right < tbRect.Left + tbRect.Width / 2)
            x = w.Value.Right + gap;
        else
            x = tbRect.Left + gap;

        bool light = Native.TaskbarLight();
        bool changed = forceRedraw || scale != lastScale || light != lastLight ||
                       lastBounds.X != x || lastBounds.Y != tbRect.Top || lastBounds.Height != h ||
                       (DateTime.Now - lastRender).TotalSeconds >= 30; // keep reset countdowns fresh
        if (!Visible) { Show(); changed = true; }

        if (changed)
        {
            lastScale = scale; lastLight = light; lastRender = DateTime.Now;
            Render(x, tbRect.Top, h, scale, light);
        }
        else
        {
            // Stay above the taskbar, which re-raises itself when clicked.
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    // ---------------------------------------------------------------- drawing

    static Color LevelColor(double pct)
    {
        if (pct >= 90) return Color.FromArgb(255, 239, 83, 80);
        if (pct >= 70) return Color.FromArgb(255, 255, 167, 38);
        return Color.FromArgb(255, 102, 187, 106);
    }

    static string FormatSpan(TimeSpan t)
    {
        if (t.TotalSeconds <= 0) return "now";
        if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h";
        if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
        return Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)) + "m";
    }

    void Render(int x, int y, int h, float s, bool light)
    {
        Color fg = light ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 245, 245, 245);
        Color dim = light ? Color.FromArgb(255, 90, 90, 90) : Color.FromArgb(255, 170, 170, 170);
        Color track = light ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255);

        using (var fLabel = new Font("Segoe UI", 11f * s, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var fPct = new Font("Segoe UI Semibold", 12f * s, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var fSmall = new Font("Segoe UI", 10f * s, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var measureBmp = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(measureBmp))
        {
            mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var fmt = StringFormat.GenericTypographic;
            var now = DateTime.Now;

            int pad = (int)(8 * s);
            int blockGap = (int)(12 * s);
            var widths = new List<int>();
            var tops = new List<string>();
            var bottoms = new List<string>();
            foreach (var l in limits)
            {
                string pctText = l.Pct.HasValue ? Math.Round(l.Pct.Value) + "%" : "--";
                string resetText = l.Reset.HasValue ? FormatSpan(l.Reset.Value - now) : "";
                float wTop = mg.MeasureString(l.Label + " ", fLabel, 1000, fmt).Width + mg.MeasureString(pctText, fPct, 1000, fmt).Width;
                float wBot = mg.MeasureString(resetText, fSmall, 1000, fmt).Width;
                int bw = (int)Math.Ceiling(Math.Max(Math.Max(wTop, wBot), 46 * s));
                widths.Add(bw); tops.Add(pctText); bottoms.Add(resetText);
            }
            int width = pad * 2;
            for (int i = 0; i < widths.Count; i++) width += widths[i] + (i > 0 ? blockGap : 0);
            if (status != null) width += (int)(12 * s);

            using (var bmp = new Bitmap(width, h, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // near-transparent so the window still gets mouse input
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                float lineTop = fPct.GetHeight(g);
                float barH = Math.Max(3, 3 * s);
                float smallH = fSmall.GetHeight(g);
                float total = lineTop + 3 * s + barH + 2 * s + smallH;
                float y0 = (h - total) / 2f;

                int bx = pad;
                using (var bDim = new SolidBrush(dim))
                using (var bFg = new SolidBrush(fg))
                using (var bTrack = new SolidBrush(track))
                {
                    for (int i = 0; i < limits.Count; i++)
                    {
                        var l = limits[i];
                        int bw = widths[i];
                        float lw = g.MeasureString(l.Label + " ", fLabel, 1000, fmt).Width;
                        float labelY = y0 + (fPct.GetHeight(g) - fLabel.GetHeight(g)) / 2f + 0.5f * s;
                        g.DrawString(l.Label, fLabel, bDim, bx, labelY, fmt);
                        using (var bPct = new SolidBrush(l.Pct.HasValue && l.Pct.Value >= 70 ? LevelColor(l.Pct.Value) : fg))
                            g.DrawString(tops[i], fPct, bPct, bx + lw, y0, fmt);

                        float barY = y0 + lineTop + 3 * s;
                        FillRound(g, bTrack, bx, barY, bw, barH);
                        if (l.Pct.HasValue && l.Pct.Value > 0)
                        {
                            float fw = (float)(bw * Math.Min(100, l.Pct.Value) / 100.0);
                            using (var bLvl = new SolidBrush(LevelColor(l.Pct.Value)))
                                FillRound(g, bLvl, bx, barY, Math.Max(fw, barH), barH);
                        }
                        g.DrawString(bottoms[i], fSmall, bDim, bx, barY + barH + 2 * s, fmt);
                        bx += bw + blockGap;
                    }
                }

                if (status != null)
                {
                    float r = 3f * s;
                    using (var bErr = new SolidBrush(Color.FromArgb(255, 239, 83, 80)))
                        g.FillEllipse(bErr, width - pad - 2 * r, y0 + 2 * s, 2 * r, 2 * r);
                }

                Native.ApplyLayered(Handle, bmp, x, y);
            }

            lastBounds = new Rectangle(x, y, width, h);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        tip.SetToolTip(this, BuildTooltip());
    }

    static void FillRound(Graphics g, Brush b, float x, float y, float w, float h)
    {
        using (var p = new GraphicsPath())
        {
            p.AddArc(x, y, h, h, 90, 180);
            p.AddArc(x + w - h, y, h, h, 270, 180);
            p.CloseFigure();
            g.FillPath(b, p);
        }
    }

    string BuildTooltip()
    {
        var sb = new StringBuilder("Claude usage limits\n");
        var now = DateTime.Now;
        foreach (var l in limits)
        {
            sb.Append("\n").Append(l.Name).Append(": ");
            sb.Append(l.Pct.HasValue ? Math.Round(l.Pct.Value, 1) + "% used" : "n/a");
            if (l.Reset.HasValue)
                sb.Append("\n    resets ").Append(l.Reset.Value.ToString("ddd HH:mm")).Append(" (in ").Append(FormatSpan(l.Reset.Value - now)).Append(")");
        }
        sb.Append("\n\n");
        if (status != null) sb.Append("Problem: ").Append(status).Append("\n");
        if (lastOk.HasValue) sb.Append("Updated ").Append(lastOk.Value.ToString("HH:mm")).Append(". ");
        sb.Append("Click to refresh, right-click for options.");
        return sb.ToString();
    }

    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new Mutex(true, "Local\\ClaudeTray-SingleInstance", out created))
        {
            if (!created) return;
            Native.MakeDpiAware();
            Application.EnableVisualStyles();
            Application.Run(new TrayForm());
        }
    }
}

static class Native
{
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pt, ref SIZE sz,
        IntPtr src, ref POINT srcPt, int key, ref BLENDFUNCTION blend, int flags);

    public static void MakeDpiAware()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }
        try { SetProcessDPIAware(); } catch { }
    }

    public static uint DpiFor(IntPtr h)
    {
        try { uint d = GetDpiForWindow(h); if (d > 0) return d; } catch { }
        return 96;
    }

    public static bool TaskbarLight()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                return v is int && (int)v == 1;
            }
        }
        catch { return false; }
    }

    // Pushes a 32-bit ARGB bitmap to a layered window (per-pixel alpha), also moving/resizing it.
    public static void ApplyLayered(IntPtr hwnd, Bitmap bmp, int x, int y)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var dst = new POINT(x, y);
            var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 /*AC_SRC_ALPHA*/ };
            UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
