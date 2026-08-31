// OffWorkCountdown — 下班倒數系統匣程式(單檔版・八角形進度邊框)
//
// .NET 10 / WinForms,無視窗。登入後自動啟動,從 Windows 事件記錄檔推算當天開工時間,
// 在系統匣畫一顆八角形圖示倒數剩餘工時,時間到就自己關掉。
//
// 圖示設計:
//   八條邊代表八等分的工時。每完成 1/8 就有一條邊由紅轉藍,由正上方順時針推進。
//   中間的數字才是精確資訊,邊框只提供「今天過了多少」的餘光感受。
//
// 需要的 csproj 設定:
//   <OutputType>WinExe</OutputType>
//   <TargetFramework>net10.0-windows</TargetFramework>
//   <UseWindowsForms>true</UseWindowsForms>

using System.Diagnostics.Eventing.Reader;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace OffWorkCountdown;

// ═══════════════════════════════════════════════════════════════════════
//  進入點
// ═══════════════════════════════════════════════════════════════════════

internal static class Program
{
    private const string MutexName = @"Local\OffWorkCountdown.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // 放在「啟動」資料夾難免會被重複觸發,確保只有一個執行個體。
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance) return;

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);

        Application.Run(new TrayContext(AppOptions.Load()));
    }

    /// <summary>
    /// 沒有主視窗可以顯示錯誤,所以寫檔。
    /// 使用者發現圖示不見時,可以到 %LOCALAPPDATA%\OffWorkCountdown 找原因。
    /// </summary>
    private static void Report(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OffWorkCountdown");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // 連寫檔都失敗就放棄,不要在沒有介面的程式裡再丟例外。
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  設定
// ═══════════════════════════════════════════════════════════════════════

/// <summary>圖示動畫模式。</summary>
public enum AnimationMode
{
    /// <summary>完全靜止,只在數字或進度變化時重畫。</summary>
    Off,

    /// <summary>只有進入警戒階段(剩餘 WarnMinutes 以內)才動。平時零成本。</summary>
    Endgame,

    /// <summary>整天都動。秒針每分鐘繞一圈。</summary>
    Always
}

/// <summary>開工時間的判定策略。</summary>
public enum StartStrategy
{
    /// <summary>當天事件記錄檔中的第一筆事件。</summary>
    FirstEventOfDay,

    /// <summary>優先採用當天的「開機」或「從睡眠喚醒」事件,找不到才退回第一筆事件。
    /// 電腦整夜不關機時,這個策略比較準。</summary>
    BootOrWake
}

public sealed class AppOptions
{
    /// <summary>一天要工作幾小時(不含午休)。</summary>
    public double WorkHours { get; set; } = 8.0;

    /// <summary>午休起訖。只有「開工時間 ~ 現在」與這個區間重疊的部分才會被扣掉。</summary>
    public string LunchStart { get; set; } = "12:00";
    public string LunchEnd { get; set; } = "13:00";

    /// <summary>最早開工時間。事件記錄推算出的時間若早於此,一律修正成這個時間,
    /// 避免電腦整夜開著導致「開工時間 = 00:00」。</summary>
    public string EarliestStart { get; set; } = "07:00";

    /// <summary>剩餘時間低於幾分鐘時彈出提示泡泡。</summary>
    public int WarnMinutes { get; set; } = 10;

    /// <summary>倒數歸零後,再等幾秒才真的結束程式(讓最後那顆泡泡有時間被看到)。</summary>
    public int ExitDelaySeconds { get; set; } = 12;

    /// <summary>要讀取的事件記錄檔。System 一般使用者即可讀取,Security 需要系統管理員。</summary>
    public string EventLogName { get; set; } = "System";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StartStrategy Strategy { get; set; } = StartStrategy.FirstEventOfDay;

    // ---- 圖示外觀 ----

    /// <summary>邊框的邊數。8 = 八角形。工時會被平均切成這麼多份,每完成一份就有一條邊變藍。</summary>
    public int SegmentCount { get; set; } = 8;

    /// <summary>進行中的那條邊是否依比例部分著色。false 則整點才整條跳色。</summary>
    public bool SmoothSegment { get; set; } = true;

    /// <summary>圖示邊長,0 = 依 DPI 自動決定(100% 為 16px)。
    /// 八角形分段在 16px 下很難數清楚,想清楚一點可以設 24,代價是 Windows 縮放後字略糊。</summary>
    public int IconSize { get; set; } = 0;

    /// <summary>分鐘是否加上 m 字尾。16px 塞不下三個字元(「45m」會整個看不見),
    /// 所以預設關閉:有字母就是小時,純數字就是分鐘。IconSize 設到 24 以上再開啟。</summary>
    public bool ShowMinuteSuffix { get; set; } = false;

    /// <summary>動畫模式。Endgame(預設)只在最後警戒階段動起來,平時完全靜止。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnimationMode Animation { get; set; } = AnimationMode.Endgame;

    /// <summary>使用電池時是否仍播放動畫。預設關閉——每秒喚醒 CPU 八小時對續航是實打實的成本。</summary>
    public bool AnimateOnBattery { get; set; } = false;

    [JsonIgnore] public TimeSpan WorkDuration => TimeSpan.FromHours(WorkHours);
    [JsonIgnore] public TimeSpan LunchStartTime => ParseTime(LunchStart, new TimeSpan(12, 0, 0));
    [JsonIgnore] public TimeSpan LunchEndTime => ParseTime(LunchEnd, new TimeSpan(13, 0, 0));
    [JsonIgnore] public TimeSpan EarliestStartTime => ParseTime(EarliestStart, new TimeSpan(7, 0, 0));
    [JsonIgnore] public TimeSpan WarnThreshold => TimeSpan.FromMinutes(Math.Max(1, WarnMinutes));
    [JsonIgnore] public int Sides => Math.Clamp(SegmentCount, 3, 12);

    private static TimeSpan ParseTime(string value, TimeSpan fallback)
        => TimeSpan.TryParse(value, out var t) ? t : fallback;

    /// <summary>讀取 exe 旁邊的 appsettings.json;缺檔或格式錯誤都退回預設值。</summary>
    public static AppOptions Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var path = Path.Combine(dir, "appsettings.json");
            if (!File.Exists(path)) return new AppOptions();

            return JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new AppOptions();
        }
        catch
        {
            return new AppOptions();
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  工時計算
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// 工時計算。午休採「已經過的部分才扣」:
/// 早上 10 點看的時候不會先預扣午休,中午 12:30 只扣 30 分鐘,過了 13:00 才扣滿 1 小時。
/// </summary>
public sealed class WorkClock(DateTime start, AppOptions options)
{
    private readonly AppOptions _options = options;

    public DateTime Start { get; private set; } = start;

    /// <summary>開工時間的來源說明,顯示在選單上。</summary>
    public string StartSource { get; set; } = string.Empty;

    public void Shift(TimeSpan delta)
    {
        Start = Start.Add(delta);
        StartSource = "手動調整";
    }

    private DateTime LunchFrom => Start.Date + _options.LunchStartTime;
    private DateTime LunchTo => Start.Date + _options.LunchEndTime;

    /// <summary>到目前為止已經扣掉的午休時間。</summary>
    public TimeSpan LunchTaken(DateTime now) => Overlap(Start, now, LunchFrom, LunchTo);

    /// <summary>實際工時。</summary>
    public TimeSpan Worked(DateTime now)
    {
        var elapsed = now - Start;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed - LunchTaken(now);
    }

    /// <summary>距離下班還有多久。</summary>
    public TimeSpan Remaining(DateTime now) => _options.WorkDuration - Worked(now);

    /// <summary>已完成的工時比例,0 ~ 1。邊框進度就是拿這個值去乘邊數。</summary>
    public double Progress(DateTime now)
    {
        var total = _options.WorkDuration.TotalSeconds;
        if (total <= 0) return 1.0;
        return Math.Clamp(Worked(now).TotalSeconds / total, 0.0, 1.0);
    }

    /// <summary>預計下班時刻(把會經過的午休加回去)。</summary>
    public DateTime ExpectedEnd
    {
        get
        {
            var end = Start + _options.WorkDuration;
            // 加回午休後可能又跨進午休區間,兩次迭代對單一午休區間已足夠收斂。
            end += Overlap(Start, end, LunchFrom, LunchTo);
            return Start + _options.WorkDuration + Overlap(Start, end, LunchFrom, LunchTo);
        }
    }

    private static TimeSpan Overlap(DateTime aFrom, DateTime aTo, DateTime bFrom, DateTime bTo)
    {
        var from = aFrom > bFrom ? aFrom : bFrom;
        var to = aTo < bTo ? aTo : bTo;
        return to > from ? to - from : TimeSpan.Zero;
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  開工時間偵測
// ═══════════════════════════════════════════════════════════════════════

public readonly record struct WorkStart(DateTime Time, string Source);

/// <summary>從 Windows 事件記錄檔推算當天的開工時間。</summary>
public static class WorkStartResolver
{
    public static WorkStart Resolve(AppOptions options)
    {
        var today = DateTime.Today;
        DateTime? found = null;
        string source = string.Empty;

        if (options.Strategy == StartStrategy.BootOrWake)
        {
            found = QueryEarliest(options.EventLogName, BootOrWakeFilter(today));
            if (found.HasValue) source = "當天開機/喚醒事件";
        }

        if (!found.HasValue)
        {
            found = QueryEarliest(options.EventLogName, AnyEventFilter(today));
            if (found.HasValue) source = $"{options.EventLogName} 記錄檔當天第一筆事件";
        }

        if (!found.HasValue)
        {
            // 事件記錄讀不到(權限、記錄檔被清空、服務停用),退回程式啟動時間。
            found = DateTime.Now;
            source = "讀不到事件記錄,改用程式啟動時間";
        }

        var floor = today + options.EarliestStartTime;
        if (found.Value < floor)
        {
            found = floor;
            source += $",已修正為最早開工 {options.EarliestStart}";
        }

        return new WorkStart(found.Value, source);
    }

    private static string Iso(DateTime localMidnight)
        => localMidnight.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string AnyEventFilter(DateTime today)
        => $"*[System[TimeCreated[@SystemTime>='{Iso(today)}']]]";

    /// <summary>
    /// 開機:EventLog 6005(事件記錄服務啟動)。
    /// 喚醒:Microsoft-Windows-Power-Troubleshooter 事件 1(系統從低耗電狀態返回)。
    /// 兩者都在 System 記錄檔,一般使用者權限即可讀取。
    /// </summary>
    private static string BootOrWakeFilter(DateTime today)
        => $"*[System[TimeCreated[@SystemTime>='{Iso(today)}'] and (" +
           "(Provider[@Name='EventLog'] and EventID=6005) or " +
           "(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1)" +
           ")]]";

    private static DateTime? QueryEarliest(string logName, string xPathFilter)
    {
        try
        {
            var query = new EventLogQuery(logName, PathType.LogName, xPathFilter)
            {
                ReverseDirection = false   // 由舊到新,讀第一筆就是最早的
            };

            using var reader = new EventLogReader(query);
            using EventRecord? record = reader.ReadEvent();
            return record?.TimeCreated?.ToLocalTime();
        }
        catch (EventLogException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  圖示繪製
// ═══════════════════════════════════════════════════════════════════════

/// <summary>倒數狀態。這裡只影響底色與文字判讀,進度由邊框負責。</summary>
public enum CountdownState
{
    /// <summary>剩餘 1 小時以上。</summary>
    Plenty,
    /// <summary>剩餘不到 1 小時。</summary>
    WrappingUp,
    /// <summary>剩餘不到警戒分鐘數。</summary>
    Imminent
}

/// <summary>
/// 把倒數文字畫成八角形圖示,邊框同時是工時進度條。
///
/// 顏色的表達預算全部花在邊框上,所以底色刻意保持中性深灰,只有進入警戒狀態才轉紅——
/// 一顆 16px 的圖示沒辦法同時清楚講兩件事。
/// </summary>
internal static partial class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>尚未完成的邊。亮紅,在深色與淺色工作列上都撐得住。</summary>
    private static readonly Color Pending = Color.FromArgb(0xE5, 0x48, 0x4D);

    /// <summary>已完成的邊。</summary>
    private static readonly Color Done = Color.FromArgb(0x4C, 0x9A, 0xFF);

    private static readonly Color Ink = Color.White;
    private static readonly Color FillNormal = Color.FromArgb(0x22, 0x2B, 0x33);
    private static readonly Color FillImminent = Color.FromArgb(0x7A, 0x1B, 0x17);

    /// <summary>每條邊兩端內縮的比例。留缺口比純粹換色更能幫助分辨邊數。</summary>
    private const float SegmentGap = 0.10f;

    /// <summary>系統匣圖示的建議邊長,會跟著 DPI 縮放。</summary>
    public static int PreferredSize(AppOptions options)
        => options.IconSize > 0
            ? options.IconSize
            : Math.Max(16, SystemInformation.SmallIconSize.Width);

    /// <param name="progress">已完成工時比例 0 ~ 1。</param>
    /// <param name="orbit">秒針位置 0 ~ 1(沿周長一圈)。null 表示不畫。</param>
    /// <param name="pulse">底色脈動強度 0 ~ 1。</param>
    public static Icon Create(string text, CountdownState state, double progress,
                              double? orbit, double pulse, int size, AppOptions options)
    {
        int sides = options.Sides;

        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // 邊框要承載資訊,所以畫得比純裝飾的圓框粗。
            float stroke = Math.Max(1.5f, size / 8f);
            float radius = (size - stroke) / 2f;
            float center = size / 2f;

            var vertices = Polygon(center, center, radius, sides);

            var fill = state == CountdownState.Imminent ? FillImminent : FillNormal;
            if (pulse > 0) fill = Brighten(fill, 0.22 * pulse);

            using (var brush = new SolidBrush(fill))
                g.FillPolygon(brush, vertices);

            DrawSegments(g, vertices, stroke, progress * sides, options.SmoothSegment);

            // 正多邊形在垂直中線處的可用寬度是內切圓直徑,再扣掉兩側描邊。
            float inradius = radius * (float)Math.Cos(Math.PI / sides);
            DrawFittedText(g, text, size, 2f * inradius - stroke * 1.6f);

            // 秒針畫在文字之後,才不會被文字蓋住。
            if (orbit.HasValue) DrawOrbitDot(g, vertices, stroke, orbit.Value);
        }

        return ToIcon(bitmap);
    }

    /// <summary>由正上方開始、順時針排列的頂點。第一條邊會是水平的頂邊。</summary>
    private static PointF[] Polygon(float cx, float cy, float r, int sides)
    {
        var points = new PointF[sides];
        double step = 2 * Math.PI / sides;
        double start = -Math.PI / 2 - step / 2;   // 讓頂邊置中而不是頂點朝上

        for (int i = 0; i < sides; i++)
        {
            double angle = start + step * i;
            points[i] = new PointF(
                cx + (float)(r * Math.Cos(angle)),
                cy + (float)(r * Math.Sin(angle)));
        }
        return points;
    }

    /// <param name="doneUnits">已完成的邊數(可含小數)。</param>
    private static void DrawSegments(Graphics g, PointF[] vertices, float stroke, double doneUnits, bool smooth)
    {
        using var pending = new Pen(Pending, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        using var done = new Pen(Done, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };

        for (int i = 0; i < vertices.Length; i++)
        {
            PointF a = vertices[i];
            PointF b = vertices[(i + 1) % vertices.Length];

            // 兩端各內縮一點,邊與邊之間就有缺口。
            PointF from = Lerp(a, b, SegmentGap);
            PointF to = Lerp(a, b, 1f - SegmentGap);

            double filled = Math.Clamp(doneUnits - i, 0.0, 1.0);
            if (!smooth) filled = filled >= 1.0 ? 1.0 : 0.0;

            if (filled <= 0.0)
            {
                g.DrawLine(pending, from, to);
            }
            else if (filled >= 1.0)
            {
                g.DrawLine(done, from, to);
            }
            else
            {
                PointF split = Lerp(from, to, (float)filled);
                g.DrawLine(done, from, split);
                g.DrawLine(pending, split, to);
            }
        }
    }

    /// <summary>
    /// 沿著多邊形周長跑的白點,一分鐘一圈。
    ///
    /// 這是整顆圖示唯一「每秒都看得到變化」的元素:16px 的周長約 49px,
    /// 一分鐘一圈等於每秒移動 0.8px,剛好在可察覺的邊緣。
    /// 進度環本身是動不了的——八小時走完同樣的周長,每秒只有 0.0017px。
    /// </summary>
    private static void DrawOrbitDot(Graphics g, PointF[] vertices, float stroke, double t)
    {
        double u = (t - Math.Floor(t)) * vertices.Length;
        int i = Math.Min((int)u, vertices.Length - 1);
        PointF p = Lerp(vertices[i], vertices[(i + 1) % vertices.Length], (float)(u - i));

        float r = stroke * 0.62f;
        using var brush = new SolidBrush(Ink);
        g.FillEllipse(brush, p.X - r, p.Y - r, r * 2f, r * 2f);
    }

    private static Color Brighten(Color c, double amount)
        => Color.FromArgb(c.A,
            (int)(c.R + (255 - c.R) * amount),
            (int)(c.G + (255 - c.G) * amount),
            (int)(c.B + (255 - c.B) * amount));

    private static PointF Lerp(PointF a, PointF b, float t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>由大往小試字級,直到塞得進內切圓的內接方框為止。</summary>
    private static void DrawFittedText(Graphics g, string text, int size, float budget)
    {
        if (string.IsNullOrEmpty(text) || budget <= 0) return;

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
        };
        using var brush = new SolidBrush(Ink);
        var box = new RectangleF(0, 0, size, size);

        for (float em = size * 0.80f; em >= 4f; em -= 0.5f)
        {
            using var font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, font, PointF.Empty, format);
            if (measured.Width <= budget && measured.Height <= size)
            {
                g.DrawString(text, font, brush, box, format);
                return;
            }
        }

        using var smallest = new Font("Segoe UI", 4f, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString(text, smallest, brush, box, format);
    }

    /// <summary>
    /// Bitmap.GetHicon() 產生的是非受管理的圖示控制代碼,一定要 DestroyIcon,
    /// 否則持續更新圖示會累積 GDI 物件。
    /// </summary>
    private static Icon ToIcon(Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  系統匣主控制
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// 整支程式的主體。沒有任何視窗,只有一顆系統匣圖示和一個右鍵選單。
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    /// <summary>進度量化的階數。每條邊切 8 段,避免每次 tick 都在重畫圖示。</summary>
    private const int ProgressSteps = 64;

    // 更新頻率是分級的。整天以 1Hz 重畫圖示等於連續八小時阻止 CPU 進入深層省電狀態,
    // 而剩 6 小時的時候畫面上其實什麼都不會變——沒有理由付這個代價。
    private const int IdleInterval = 60_000;   // 剩餘 1 小時以上
    private const int NearInterval = 5_000;    // 剩餘 1 小時內
    private const int TickInterval = 1_000;    // Always 模式
    private const int SmoothInterval = 200;    // 警戒階段,脈動才不會頻閃

    private readonly AppOptions _options;
    private readonly NotifyIcon _icon;
    private readonly WinFormsTimer _timer;
    private readonly ToolStripMenuItem _startItem;

    private WorkClock _clock;
    private Icon? _currentIcon;
    private string _lastRendered = string.Empty;
    private bool _warned;
    private bool _finishing;

    public TrayContext(AppOptions options)
    {
        _options = options;

        var start = WorkStartResolver.Resolve(_options);
        _clock = new WorkClock(start.Time, _options) { StartSource = start.Source };

        _startItem = new ToolStripMenuItem { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_startItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("開工時間往前 15 分鐘", null, (_, _) => Adjust(TimeSpan.FromMinutes(-15)));
        menu.Items.Add("開工時間往後 15 分鐘", null, (_, _) => Adjust(TimeSpan.FromMinutes(15)));
        menu.Items.Add("重新偵測開工時間", null, (_, _) => Redetect());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "下班倒數"
        };
        _icon.MouseClick += OnIconClick;

        _timer = new WinFormsTimer { Interval = NearInterval };
        _timer.Tick += (_, _) => Refresh();

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.TimeChanged += OnTimeChanged;

        Refresh();
        _timer.Start();
    }

    // ---------- 倒數 ----------

    private void Refresh()
    {
        if (_finishing) return;

        var now = DateTime.Now;

        // 跨日(例如電腦整晚沒關、程式跑過午夜)就重新推算一次。
        if (now.Date != _clock.Start.Date)
        {
            Redetect();
            return;
        }

        var remaining = _clock.Remaining(now);

        if (remaining <= TimeSpan.Zero)
        {
            Finish();
            return;
        }

        var state = remaining <= _options.WarnThreshold ? CountdownState.Imminent
                  : remaining < TimeSpan.FromHours(1) ? CountdownState.WrappingUp
                  : CountdownState.Plenty;

        bool animate = ShouldAnimate(state);

        // 秒針:每分鐘一圈。用毫秒才不會在 5Hz 更新下卡成一格一格。
        double? orbit = animate
            ? (now.Second + now.Millisecond / 1000.0) / 60.0
            : null;

        // 脈動只在警戒階段,週期 2 秒。
        double pulse = animate && state == CountdownState.Imminent
            ? 0.5 + 0.5 * Math.Sin(now.TimeOfDay.TotalSeconds * Math.PI)
            : 0.0;

        SetInterval(state, remaining, animate);
        RenderIcon(FormatBadge(remaining), state, _clock.Progress(now), orbit, pulse);
        UpdateTooltip(remaining, now);

        if (state == CountdownState.Imminent && !_warned)
        {
            _warned = true;
            _icon.ShowBalloonTip(15_000, "下班倒數", "準備下班嘍", ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// 圖示上的文字。超過 1 小時是 "7H",不到 1 小時是 "45"(或 "45m",看設定)。
    /// 小時無條件捨去、分鐘無條件進位並上限 59,銜接時才不會出現 "60m"。
    ///
    /// 一律控制在兩個字元內:16px 的圖示放不下三個字元,硬塞會縮到完全看不見。
    /// 有字母代表小時,純數字代表分鐘。
    /// </summary>
    private string FormatBadge(TimeSpan remaining)
    {
        if (remaining >= TimeSpan.FromHours(1))
            return $"{(int)remaining.TotalHours}H";

        int minutes = Math.Clamp((int)Math.Ceiling(remaining.TotalMinutes), 1, 59);
        return _options.ShowMinuteSuffix ? $"{minutes}m" : minutes.ToString();
    }

    /// <summary>目前狀態該不該播動畫。</summary>
    private bool ShouldAnimate(CountdownState state)
    {
        if (_options.Animation == AnimationMode.Off) return false;

        if (!_options.AnimateOnBattery &&
            SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
            return false;

        return _options.Animation == AnimationMode.Always
            || state == CountdownState.Imminent;
    }

    private void SetInterval(CountdownState state, TimeSpan remaining, bool animate)
    {
        int desired =
            animate && state == CountdownState.Imminent ? SmoothInterval
            : animate ? TickInterval
            : remaining < TimeSpan.FromHours(1) ? NearInterval
            : IdleInterval;

        if (_timer.Interval != desired) _timer.Interval = desired;
    }

    private void RenderIcon(string text, CountdownState state, double progress,
                            double? orbit = null, double pulse = 0.0)
    {
        // 全部量化後再比對。沒有這一層,每次 tick 都會因為浮點微差而重新產生 GDI 物件;
        // 有了它,靜止狀態下一整個小時可能只重畫幾次。
        int progressBucket = (int)Math.Round(progress * ProgressSteps);
        int orbitBucket = orbit.HasValue ? (int)Math.Round(orbit.Value * 120) % 120 : -1;
        int pulseBucket = (int)Math.Round(pulse * 8);

        var key = $"{text}|{state}|{progressBucket}|{orbitBucket}|{pulseBucket}";
        if (key == _lastRendered) return;
        _lastRendered = key;

        var previous = _currentIcon;
        _currentIcon = TrayIconFactory.Create(
            text, state, (double)progressBucket / ProgressSteps,
            orbit, pulse,
            TrayIconFactory.PreferredSize(_options), _options);
        _icon.Icon = _currentIcon;
        previous?.Dispose();
    }

    private void UpdateTooltip(TimeSpan remaining, DateTime now)
    {
        int sides = _options.Sides;
        int litEdges = (int)(_clock.Progress(now) * sides);

        // NotifyIcon.Text 上限 63 字元,超過會丟例外。
        var tip = $"剩 {(int)remaining.TotalHours} 時 {remaining.Minutes} 分・{litEdges}/{sides} 邊"
                + $"\r\n開工 {_clock.Start:HH:mm}・預計 {_clock.ExpectedEnd:HH:mm} 下班";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        _startItem.Text = $"開工 {_clock.Start:HH:mm}({_clock.StartSource})";
    }

    // ---------- 選單動作 ----------

    private void Adjust(TimeSpan delta)
    {
        _clock.Shift(delta);
        _warned = false;
        _lastRendered = string.Empty;
        Refresh();
    }

    private void Redetect()
    {
        var start = WorkStartResolver.Resolve(_options);
        _clock = new WorkClock(start.Time, _options) { StartSource = start.Source };
        _warned = false;
        _finishing = false;
        _lastRendered = string.Empty;
        Refresh();
    }

    private void OnIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var now = DateTime.Now;
        var remaining = _clock.Remaining(now);
        var worked = _clock.Worked(now);
        var lunch = _clock.LunchTaken(now);
        int sides = _options.Sides;

        _icon.ShowBalloonTip(8_000, "下班倒數",
            $"開工 {_clock.Start:HH:mm}({_clock.StartSource})\n" +
            $"已工作 {(int)worked.TotalHours} 小時 {worked.Minutes} 分,已扣午休 {(int)lunch.TotalMinutes} 分\n" +
            $"邊框進度 {(int)(_clock.Progress(now) * sides)}/{sides}\n" +
            $"還剩 {(int)remaining.TotalHours} 小時 {remaining.Minutes} 分,預計 {_clock.ExpectedEnd:HH:mm} 下班",
            ToolTipIcon.None);
    }

    // ---------- 系統事件 ----------

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Resume 要立刻更新;StatusChange 代表插拔電源,會影響動畫是否開啟。
        if (e.Mode is PowerModes.Resume or PowerModes.StatusChange) Refresh();
    }

    private void OnTimeChanged(object? sender, EventArgs e) => Refresh();

    // ---------- 結束 ----------

    private void Finish()
    {
        _finishing = true;
        _timer.Stop();

        // 收工時八條邊全藍,並停掉動畫。
        RenderIcon("OK", CountdownState.Imminent, 1.0);
        _icon.Text = "工時已滿,收工";
        _icon.ShowBalloonTip(10_000, "下班倒數", "工時已滿,收工囉", ToolTipIcon.Info);

        var exitTimer = new WinFormsTimer
        {
            Interval = Math.Max(1, _options.ExitDelaySeconds) * 1000
        };
        exitTimer.Tick += (_, _) =>
        {
            exitTimer.Stop();
            exitTimer.Dispose();
            ExitThread();
        };
        exitTimer.Start();
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;

        _timer.Stop();
        _timer.Dispose();

        // 沒有這行,圖示會殘留在系統匣直到滑鼠滑過去。
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();

        base.ExitThreadCore();
    }
}
