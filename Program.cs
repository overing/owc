// OffWorkCountdown — 下班倒數系統匣程式(單檔版・圓環進度邊框)
//
// .NET 10 / WinForms,無視窗。登入後自動啟動,從 Windows 事件記錄檔推算當天開工時間,
// 在系統匣畫一顆圓形圖示倒數剩餘工時,工時滿了就改為累計超時工作時間。
//
// 圖示設計:
//   外圈是一整圈進度環。工時走到哪,藍色就從正上方順時針把紅色蓋到哪。
//   工時滿了是整圈藍,超時之後同樣由正上方順時針染回紅色。
//   中間的數字才是精確資訊,圓環只提供「今天過了多少」的餘光感受。
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

    /// <summary>要讀取的事件記錄檔。System 一般使用者即可讀取,Security 需要系統管理員。</summary>
    public string EventLogName { get; set; } = "System";

    [JsonConverter(typeof(JsonStringEnumConverter<StartStrategy>))]
    public StartStrategy Strategy { get; set; } = StartStrategy.FirstEventOfDay;

    // ---- 圖示外觀 ----

    /// <summary>圖示邊長,0 = 依 DPI 自動決定(100% 為 16px)。
    /// 16px 的圓周只有 44px 上下,想把進度看得更細可以設 24,代價是 Windows 縮放後字略糊。</summary>
    public int IconSize { get; set; } = 0;

    /// <summary>分鐘是否加上 m 字尾。16px 塞不下三個字元(「45m」會整個看不見),
    /// 所以預設關閉:有字母就是小時,純數字就是分鐘。IconSize 設到 24 以上再開啟。</summary>
    public bool ShowMinuteSuffix { get; set; } = false;

    /// <summary>動畫模式。Endgame(預設)只在最後警戒階段動起來,平時完全靜止。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AnimationMode>))]
    public AnimationMode Animation { get; set; } = AnimationMode.Endgame;

    /// <summary>使用電池時是否仍播放動畫。預設關閉——每秒喚醒 CPU 八小時對續航是實打實的成本。</summary>
    public bool AnimateOnBattery { get; set; } = false;

    // ---- 圓環配色 ----
    // 都吃 #RRGGBB 或 #AARRGGBB,留空或打錯字一律安靜退回預設。

    /// <summary>已完成的藍弧。</summary>
    public string ColorDone { get; set; } = "#4C9AFF";

    /// <summary>還沒完成、以及超時被染回的紅弧。</summary>
    public string ColorPending { get; set; } = "#E5484D";

    /// <summary>圖示上的文字與秒針點。</summary>
    public string ColorText { get; set; } = "#FFFFFF";

    /// <summary>平時的中性底色。</summary>
    public string ColorFill { get; set; } = "#222B33";

    /// <summary>警戒／超時的底色。</summary>
    public string ColorFillImminent { get; set; } = "#7A1B17";

    [JsonIgnore] public TimeSpan WorkDuration => TimeSpan.FromHours(WorkHours);
    [JsonIgnore] public TimeSpan LunchStartTime => ParseTime(LunchStart, new TimeSpan(12, 0, 0));
    [JsonIgnore] public TimeSpan LunchEndTime => ParseTime(LunchEnd, new TimeSpan(13, 0, 0));
    [JsonIgnore] public TimeSpan EarliestStartTime => ParseTime(EarliestStart, new TimeSpan(7, 0, 0));
    [JsonIgnore] public TimeSpan WarnThreshold => TimeSpan.FromMinutes(Math.Max(1, WarnMinutes));

    [JsonIgnore] public Color DoneColor => ParseColor(ColorDone, Color.FromArgb(0x4C, 0x9A, 0xFF));
    [JsonIgnore] public Color PendingColor => ParseColor(ColorPending, Color.FromArgb(0xE5, 0x48, 0x4D));
    [JsonIgnore] public Color TextColor => ParseColor(ColorText, Color.White);
    [JsonIgnore] public Color FillColor => ParseColor(ColorFill, Color.FromArgb(0x22, 0x2B, 0x33));
    [JsonIgnore] public Color FillImminentColor => ParseColor(ColorFillImminent, Color.FromArgb(0x7A, 0x1B, 0x17));

    private static TimeSpan ParseTime(string value, TimeSpan fallback)
        => TimeSpan.TryParse(value, out var t) ? t : fallback;

    private static Color ParseColor(string value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try { return ColorTranslator.FromHtml(value.Trim()); }
        catch { return fallback; }
    }

    /// <summary>讀取 exe 旁邊的 appsettings.json;缺檔或格式錯誤都退回預設值。</summary>
    public static AppOptions Load()
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var path = Path.Combine(dir, "appsettings.json");
            if (!File.Exists(path)) return new AppOptions();

            return JsonSerializer.Deserialize(File.ReadAllText(path), AppOptionsContext.Default.AppOptions)
                   ?? new AppOptions();
        }
        catch
        {
            return new AppOptions();
        }
    }
}

/// <summary>
/// appsettings.json 的 System.Text.Json 原始碼產生器內容。
///
/// AOT／裁剪過的組件不能用反射式序列化——那條路會在執行期直接丟 NotSupportedException,
/// 被 <see cref="AppOptions.Load"/> 的 try/catch 吞掉,結果是所有設定靜默失效、全用預設值。
/// 改走原始碼產生器後,設定才真的讀得到,順帶把反射序列化器整包從輸出裁掉。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppOptions))]
internal partial class AppOptionsContext : JsonSerializerContext;

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

    /// <summary>已完成的工時比例,0 ~ 1。圓環的藍弧就是拿這個值去乘一整圈。</summary>
    public double Progress(DateTime now)
    {
        var total = _options.WorkDuration.TotalSeconds;
        if (total <= 0) return 1.0;
        return Math.Clamp(Worked(now).TotalSeconds / total, 0.0, 1.0);
    }

    /// <summary>超時時間。工時還沒滿就是零。</summary>
    public TimeSpan Overtime(DateTime now)
    {
        var remaining = Remaining(now);
        return remaining < TimeSpan.Zero ? -remaining : TimeSpan.Zero;
    }

    /// <summary>超時佔一份完整工時的比例,0 ~ 1。圓環就是拿這個值把藍弧依序染回紅色。</summary>
    public double OvertimeProgress(DateTime now)
    {
        var total = _options.WorkDuration.TotalSeconds;
        if (total <= 0) return 1.0;
        return Math.Clamp(Overtime(now).TotalSeconds / total, 0.0, 1.0);
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
    Imminent,
    /// <summary>工時已滿,正在累計超時。</summary>
    Overtime
}

/// <summary>
/// 把倒數文字畫成圓形圖示,外圈同時是工時進度環。
///
/// 顏色的表達預算全部花在圓環上,所以底色刻意保持中性深灰,只有進入警戒狀態才轉紅——
/// 一顆 16px 的圖示沒辦法同時清楚講兩件事。
/// </summary>
internal static partial class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>圖示配色,由 <see cref="AppOptions"/> 解析而來。</summary>
    public readonly record struct Palette(Color Done, Color Pending, Color Ink, Color Fill, Color FillImminent);

    /// <summary>系統匣圖示的建議邊長,會跟著 DPI 縮放。</summary>
    public static int PreferredSize(AppOptions options)
        => options.IconSize > 0
            ? options.IconSize
            : Math.Max(16, SystemInformation.SmallIconSize.Width);

    /// <param name="progress">已完成工時比例 0 ~ 1。</param>
    /// <param name="overtime">超時比例 0 ~ 1。會把已完成的藍弧由頭依序染回紅色。</param>
    /// <param name="orbit">秒針位置 0 ~ 1(沿圓周一圈)。null 表示不畫。</param>
    /// <param name="pulse">底色脈動強度 0 ~ 1。</param>
    public static Icon Create(string text, CountdownState state, double progress, double overtime,
                              double? orbit, double pulse, int size, Palette palette)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // 圓環要承載資訊,所以畫得比純裝飾的細框粗。
            float stroke = Math.Max(1.5f, size / 8f);
            float radius = (size - stroke) / 2f;
            float center = size / 2f;

            // 描邊以這個圓為中心線,一半往外一半往內,所以底色也填到這裡為止。
            var ring = new RectangleF(center - radius, center - radius, radius * 2f, radius * 2f);

            var fill = state is CountdownState.Imminent or CountdownState.Overtime ? palette.FillImminent : palette.Fill;
            if (pulse > 0) fill = Brighten(fill, 0.22 * pulse);

            using (var brush = new SolidBrush(fill))
                g.FillEllipse(brush, ring);

            DrawRing(g, ring, stroke, progress, overtime, palette.Done, palette.Pending);

            // 圓在垂直中線處的可用寬度是直徑,再扣掉兩側描邊。
            DrawFittedText(g, text, size, 2f * radius - stroke * 1.6f, palette.Ink);

            // 秒針畫在文字之後,才不會被文字蓋住。
            if (orbit.HasValue) DrawOrbitDot(g, center, radius, stroke, orbit.Value, palette.Ink);
        }

        return ToIcon(bitmap);
    }

    /// <summary>
    /// 進度環。整圈最多三段:超時染回的紅、還沒被染回的藍、還沒完成的紅,
    /// 三段首尾相接,一條線繞完整個圓周。
    /// </summary>
    /// <param name="progress">已完成工時比例 0 ~ 1。</param>
    /// <param name="overtime">超時後被染回紅色的比例 0 ~ 1,同樣由正上方順時針推進。</param>
    private static void DrawRing(Graphics g, RectangleF ring, float stroke, double progress, double overtime,
                                 Color doneColor, Color pendingColor)
    {
        using var pending = new Pen(pendingColor, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        using var done = new Pen(doneColor, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };

        double filled = Math.Clamp(progress, 0.0, 1.0);
        // 還沒藍過的地方談不上染回。
        double reverted = Math.Min(Math.Clamp(overtime, 0.0, 1.0), filled);

        DrawArc(g, pending, ring, 0.0, reverted);
        DrawArc(g, done, ring, reverted, filled);
        DrawArc(g, pending, ring, filled, 1.0);
    }

    /// <summary>畫圓周上 [a, b] 這一段,0 是正上方、順時針前進。太短就整段跳過。</summary>
    private static void DrawArc(Graphics g, Pen pen, RectangleF ring, double a, double b)
    {
        if (b - a <= 1e-4) return;
        g.DrawArc(pen, ring, (float)(-90.0 + a * 360.0), (float)((b - a) * 360.0));
    }

    /// <summary>
    /// 沿著圓周跑的白點,一分鐘一圈。
    ///
    /// 這是整顆圖示唯一「每秒都看得到變化」的元素:16px 的圓周約 44px,
    /// 一分鐘一圈等於每秒移動 0.7px,剛好在可察覺的邊緣。
    /// 進度環本身是動不了的——八小時走完同樣的圓周,每秒只有 0.0015px。
    /// </summary>
    private static void DrawOrbitDot(Graphics g, float center, float radius, float stroke, double t, Color ink)
    {
        double angle = -Math.PI / 2 + (t - Math.Floor(t)) * 2 * Math.PI;
        float x = center + (float)(radius * Math.Cos(angle));
        float y = center + (float)(radius * Math.Sin(angle));

        float r = stroke * 0.62f;
        using var brush = new SolidBrush(ink);
        g.FillEllipse(brush, x - r, y - r, r * 2f, r * 2f);
    }

    private static Color Brighten(Color c, double amount)
        => Color.FromArgb(c.A,
            (int)(c.R + (255 - c.R) * amount),
            (int)(c.G + (255 - c.G) * amount),
            (int)(c.B + (255 - c.B) * amount));

    /// <summary>由大往小試字級,直到塞得進內切圓的內接方框為止。</summary>
    private static void DrawFittedText(Graphics g, string text, int size, float budget, Color ink)
    {
        if (string.IsNullOrEmpty(text) || budget <= 0) return;

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
        };
        using var brush = new SolidBrush(ink);
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
    /// <summary>進度量化的階數。整圈切 64 階(16px 下約每 0.7px 一階),避免每次 tick 都在重畫圖示。</summary>
    private const int ProgressSteps = 64;

    // 更新頻率是分級的。整天以 1Hz 重畫圖示等於連續八小時阻止 CPU 進入深層省電狀態,
    // 而剩 6 小時的時候畫面上其實什麼都不會變——沒有理由付這個代價。
    private const int IdleInterval = 60_000;   // 剩餘 1 小時以上
    private const int NearInterval = 5_000;    // 剩餘 1 小時內
    private const int TickInterval = 1_000;    // Always 模式
    private const int SmoothInterval = 200;    // 警戒階段,脈動才不會頻閃

    private readonly AppOptions _options;
    private readonly TrayIconFactory.Palette _palette;
    private readonly NotifyIcon _icon;
    private readonly WinFormsTimer _timer;
    private readonly ToolStripMenuItem _startItem;

    private WorkClock _clock;
    private Icon? _currentIcon;
    private string _lastRendered = string.Empty;
    private bool _warned;
    private bool _overtimeAnnounced;

    public TrayContext(AppOptions options)
    {
        _options = options;
        _palette = new TrayIconFactory.Palette(
            options.DoneColor, options.PendingColor, options.TextColor,
            options.FillColor, options.FillImminentColor);

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
        var now = DateTime.Now;

        var remaining = _clock.Remaining(now);
        var overtime = _clock.Overtime(now);

        // 跨日(例如電腦整晚沒關、程式跑過午夜)就重新推算一次。
        // 超時中是例外:那是還沒下班的同一班,不是新的一天,重算會把累計的超時抹掉。
        // 代價是超時過午夜後就不會自己換日了,要換得從選單「重新偵測開工時間」。
        if (now.Date != _clock.Start.Date && remaining > TimeSpan.Zero)
        {
            Redetect();
            return;
        }

        var state = remaining <= TimeSpan.Zero ? CountdownState.Overtime
                  : remaining <= _options.WarnThreshold ? CountdownState.Imminent
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
        RenderIcon(state == CountdownState.Overtime ? FormatOvertimeBadge(overtime) : FormatBadge(remaining),
                   state, _clock.Progress(now), _clock.OvertimeProgress(now), orbit, pulse);
        UpdateTooltip(remaining, overtime);

        if (state == CountdownState.Overtime)
        {
            if (!_overtimeAnnounced)
            {
                _overtimeAnnounced = true;
                _icon.ShowBalloonTip(15_000, "下班倒數", "工時已滿,開始計算超時工作", ToolTipIcon.Info);
            }
        }
        else if (state == CountdownState.Imminent && !_warned)
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

    /// <summary>
    /// 超時的圖示文字。與倒數相反,採無條件捨去——講的是「已經超過多久」。
    /// 字面和倒數長得一樣,靠圓環染回紅色與底色轉紅來分辨。
    /// </summary>
    private string FormatOvertimeBadge(TimeSpan overtime)
    {
        if (overtime >= TimeSpan.FromHours(1))
            return $"{(int)overtime.TotalHours}H";

        int minutes = Math.Clamp((int)overtime.TotalMinutes, 0, 59);
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
            // 超時的 remaining 是負的,一樣落在這裡:每 5 秒更新,足夠跟上分鐘數與染回的進度。
            : remaining < TimeSpan.FromHours(1) ? NearInterval
            : IdleInterval;

        if (_timer.Interval != desired) _timer.Interval = desired;
    }

    private void RenderIcon(string text, CountdownState state, double progress, double overtime,
                            double? orbit = null, double pulse = 0.0)
    {
        // 全部量化後再比對。沒有這一層,每次 tick 都會因為浮點微差而重新產生 GDI 物件;
        // 有了它,靜止狀態下一整個小時可能只重畫幾次。
        int progressBucket = (int)Math.Round(progress * ProgressSteps);
        int overtimeBucket = (int)Math.Round(overtime * ProgressSteps);
        int orbitBucket = orbit.HasValue ? (int)Math.Round(orbit.Value * 120) % 120 : -1;
        int pulseBucket = (int)Math.Round(pulse * 8);

        var key = $"{text}|{state}|{progressBucket}|{overtimeBucket}|{orbitBucket}|{pulseBucket}";
        if (key == _lastRendered) return;
        _lastRendered = key;

        var previous = _currentIcon;
        _currentIcon = TrayIconFactory.Create(
            text, state, (double)progressBucket / ProgressSteps, (double)overtimeBucket / ProgressSteps,
            orbit, pulse,
            TrayIconFactory.PreferredSize(_options), _palette);
        _icon.Icon = _currentIcon;
        previous?.Dispose();
    }

    private void UpdateTooltip(TimeSpan remaining, TimeSpan overtime)
    {
        var head = remaining > TimeSpan.Zero
            ? $"剩 {(int)remaining.TotalHours} 時 {remaining.Minutes} 分"
            : $"超時工作 {(int)overtime.TotalHours} 時 {overtime.Minutes} 分";

        // NotifyIcon.Text 上限 63 字元,超過會丟例外。
        var tip = head
                + $"\r\n開工 {_clock.Start:HH:mm}・下班 {_clock.ExpectedEnd:HH:mm}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        _startItem.Text = $"開工 {_clock.Start:HH:mm}({_clock.StartSource})";
    }

    // ---------- 選單動作 ----------

    private void Adjust(TimeSpan delta)
    {
        _clock.Shift(delta);
        _warned = false;
        _overtimeAnnounced = false;
        _lastRendered = string.Empty;
        Refresh();
    }

    private void Redetect()
    {
        var start = WorkStartResolver.Resolve(_options);
        _clock = new WorkClock(start.Time, _options) { StartSource = start.Source };
        _warned = false;
        _overtimeAnnounced = false;
        _lastRendered = string.Empty;
        Refresh();
    }

    private void OnIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        var now = DateTime.Now;
        var remaining = _clock.Remaining(now);
        var overtime = _clock.Overtime(now);
        var worked = _clock.Worked(now);
        var lunch = _clock.LunchTaken(now);

        var tail = remaining > TimeSpan.Zero
            ? $"還剩 {(int)remaining.TotalHours} 小時 {remaining.Minutes} 分,預計 {_clock.ExpectedEnd:HH:mm} 下班"
            : $"超時工作 {(int)overtime.TotalHours} 小時 {overtime.Minutes} 分,應於 {_clock.ExpectedEnd:HH:mm} 下班";

        _icon.ShowBalloonTip(8_000, "下班倒數",
            $"開工 {_clock.Start:HH:mm}({_clock.StartSource})\n" +
            $"已工作 {(int)worked.TotalHours} 小時 {worked.Minutes} 分,已扣午休 {(int)lunch.TotalMinutes} 分\n" +
            tail,
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
    // 工時滿了不再自動結束,只有選單的「結束」會走到這裡。

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
