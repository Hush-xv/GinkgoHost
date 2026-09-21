// 无硬件冒烟测试：验证 DLL 可加载、入口点可调用、调用约定正确。
// 不插适配器也能跑；插上适配器会额外输出检测数。
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using GinkgoHost.Models;
using GinkgoHost.Native;
using GinkgoHost.Services;
using GinkgoHost.Views;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Dbg 落盘是异步的，而本程序各分支用 return 立即退出进程：注册退出钩子把队尾日志刷出去
AppDomain.CurrentDomain.ProcessExit += (_, _) => Dbg.Flush();

// ── 日志显示边界测试：--log-display，不访问硬件。 ──
// 验证长 HEX 数据完整保留、行内可截断显示的源字符串不换行，以及 5,000 条上限。
if (args.Length > 0 && args[0] == "--log-display")
{
    int displayFailures = 0;
    foreach (int length in new[] { 32, 128, 256 })
    {
        byte[] bytes = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var entry = new LogEntry(DateTime.UnixEpoch, "RX", "READ", "0x92", 0, 0.4, bytes);
        int groups = entry.HexGrouped.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        bool pass = groups == length && entry.HexGrouped.Length == length * 3 - 1;
        Console.WriteLine($"{(pass ? "PASS" : "FAIL")}  {length} 字节 HEX 保留 {groups} 组");
        if (!pass) displayFailures++;
    }

    var log = new ObservableCollection<LogEntry>();
    DateTime start = DateTime.UnixEpoch;
    for (int i = 0; i < LogCollectionExtensions.Cap + 100; i++)
        log.AddCapped(new LogEntry(start.AddMilliseconds(i), "RX", "READ", "0x92", 0, 0.1, [(byte)i]));
    bool capPass = log.Count == LogCollectionExtensions.Cap && log[0].Ts == start.AddMilliseconds(100);
    Console.WriteLine($"{(capPass ? "PASS" : "FAIL")}  日志上限 {log.Count}/{LogCollectionExtensions.Cap}，最旧记录正确淘汰");

    var appLog = new CappedLogCollection();
    int resetCount = 0, removeCount = 0;
    appLog.CollectionChanged += (_, e) =>
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resetCount++;
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove) removeCount++;
    };
    for (int i = 0; i <= LogCollectionExtensions.Cap; i++)
        appLog.AddCapped(new LogEntry(start.AddMilliseconds(i), "RX", "READ", "0x92", 0, 0.1, [(byte)i]));
    bool batchCapPass = appLog.Count == LogCollectionExtensions.Cap + 1 - 128 &&
                        appLog[0].Ts == start.AddMilliseconds(128) && resetCount == 1 && removeCount == 0;
    Console.WriteLine($"{(batchCapPass ? "PASS" : "FAIL")}  应用日志批量淘汰 count={appLog.Count} reset={resetCount} remove={removeCount}");
    if (!batchCapPass) displayFailures++;

    // SYS 数据含控制字节（如旧扫描路径塞的原始地址 0x08）回退 hex；可读文本原样展示
    var rawScan = new LogEntry(DateTime.UnixEpoch, "SYS", "总线扫描", "—", 0, 0, [0x08]);
    bool rawPass = rawScan.DataDisplay == "08";
    Console.WriteLine($"{(rawPass ? "PASS" : "FAIL")}  SYS 控制字节数据回退 hex（实际 {rawScan.DataDisplay}）");
    var textEntry = new LogEntry(DateTime.UnixEpoch, "SYS", "连接适配器", "—", 0, 0,
        System.Text.Encoding.UTF8.GetBytes("Ginkgo · CH1 · 400 kHz"));
    bool textPass = textEntry.DataDisplay == "Ginkgo · CH1 · 400 kHz";
    Console.WriteLine($"{(textPass ? "PASS" : "FAIL")}  SYS 可读文本原样展示");

    var snapshotRow = new RegRow { Value = "AA", SnapshotBaseline = "AA" };
    bool snapshotEqualPass = snapshotRow.SnapshotHint == "与快照一致 · AA";
    snapshotRow.Value = "BB";
    snapshotRow.SnapshotChanged = true;
    bool snapshotChangedPass = snapshotRow.SnapshotHint == "快照 AA  →  当前 BB";
    Console.WriteLine($"{(snapshotEqualPass && snapshotChangedPass ? "PASS" : "FAIL")}  寄存器快照提示保留前后值");

    var statusRow = new RegRow { StatusDetail = "事务执行成功", LastDurationMs = 0.74, Status = "OK" };
    bool statusPass = statusRow.StatusDisplay == "OK 0.7" &&
                      statusRow.StatusHint == "执行成功 · 耗时 0.7 ms · 事务执行成功";
    string persistedRow = System.Text.Json.JsonSerializer.Serialize(statusRow);
    bool transientPass = !persistedRow.Contains("StatusDisplay", StringComparison.Ordinal) &&
                         !persistedRow.Contains("LastDurationMs", StringComparison.Ordinal) &&
                         !persistedRow.Contains("StatusDetail", StringComparison.Ordinal);
    Console.WriteLine($"{(statusPass && transientPass ? "PASS" : "FAIL")}  行状态耗时展示且不写入 Profile");

    return displayFailures == 0 && capPass && batchCapPass && rawPass && textPass &&
           snapshotEqualPass && snapshotChangedPass && statusPass && transientPass ? 0 : 1;
}

// ── 异步日志落盘检查：--log-async [条数]，不访问硬件 ──
// Dbg 改为「入队 + 后台泵线程写盘」后，这里有三个前提必须成立，否则崩溃现场的末尾日志会静默丢失：
// 入队路径不阻塞业务线程、一次 Flush 能排空队列、泵线程按入队顺序落盘。
if (args.Length > 0 && args[0] == "--log-async")
{
    int lineCount = args.Length > 1 ? int.Parse(args[1]) : 5000;
    string tag = $"ASYNC-{Guid.NewGuid():N}";

    var swAsync = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < lineCount; i++) Dbg.Log($"{tag} #{i}");
    Dbg.Log($"{tag} #END");
    swAsync.Stop();
    // 上限给得很宽：只为捕捉「Log 又变回同步刷盘」这类回归
    bool fast = swAsync.ElapsedMilliseconds < 2000;
    Console.WriteLine($"{(fast ? "PASS" : "FAIL")}  {lineCount} 行入队耗时 {swAsync.ElapsedMilliseconds} ms（异步、不刷盘）");

    Dbg.Flush();
    string file = Path.Combine(Dbg.LogDir, $"GinkgoHost_{DateTime.Now:yyyyMMdd}.log");
    List<string> lines = [];
    if (File.Exists(file))
    {
        // 当日日志可能仍被写入端打开：必须按 ReadWrite 共享来读，File.ReadAllLines 会被拒
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } readLine)
            if (readLine.Contains(tag)) lines.Add(readLine);
    }
    bool noLoss = lines.Count == lineCount + 1;
    Console.WriteLine($"{(noLoss ? "PASS" : "FAIL")}  Flush 后落盘 {lines.Count}/{lineCount + 1} 行");

    bool inOrder = lines.Count == lineCount + 1 && lines[0].Contains($"{tag} #0") && lines[^1].EndsWith($"{tag} #END");
    Console.WriteLine($"{(inOrder ? "PASS" : "FAIL")}  泵线程按入队顺序落盘");
    Console.WriteLine($"INFO  日志文件 {file}");

    return fast && noLoss && inOrder ? 0 : 1;
}

// ── 日志派生计数一致性：--log-counters，不访问硬件 ──
// LogPanel 靠增量计数器撑住高频事务流，一旦增量与全表重算不一致，界面会显示错误的筛选命中数。
// 这里同时验证 CountFor 与 Matches 仍描述同一个筛选语义（两者分开表达时最容易漂移）。
if (args.Length > 0 && args[0] == "--log-counters")
{
    int ckFailures = 0;
    void Ck(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
        if (!ok) ckFailures++;
    }

    static LogEntry Entry(int i, string dir, int ret) =>
        new(new DateTime(2026, 1, 1).AddSeconds(i), dir, "TEST", "0x50", ret, 0.1, null);

    // 增量结果必须与「按 Matches 现场数出来」的基准完全一致，含末尾连续失败区间
    static bool Agree(LogCounters c, List<LogEntry> live)
    {
        foreach (string f in new[] { "all", "read", "write", "system", "error" })
            if (c.CountFor(f) != live.Count(e => LogCounters.Matches(f, e))) return false;
        int tail = 0;
        for (int i = live.Count - 1; i >= 0 && !live[i].Ok; i--) tail++;
        string? latest = tail > 0 ? live[^1].RetText : null;
        return c.Total == live.Count && c.ConsecutiveFailures == tail && c.LatestFailure == latest;
    }

    string[] dirs = ["RX", "TX", "SYS"];
    var seq = new List<LogEntry>();
    for (int i = 0; i < 400; i++) seq.Add(Entry(i, dirs[i % 3], i % 7 == 0 ? -10 : 0));
    for (int i = 0; i < 5; i++) seq.Add(Entry(1000 + i, "RX", -1)); // 末尾连续失败

    var counters = new LogCounters();
    var live = new List<LogEntry>();
    bool agreeWhileAdding = true;
    foreach (LogEntry e in seq)
    {
        counters.Add(e);
        live.Add(e);
        agreeWhileAdding &= Agree(counters, live);
    }
    Ck("增量 Add 与全表基准一致（含末尾连续失败）", agreeWhileAdding);

    // 模拟环形缓冲淘汰最旧记录：复刻 LogPanel 的判定——只有删除碰到末尾失败区间才重扫尾巴
    bool agreeWhileEvicting = true;
    while (live.Count > 0)
    {
        int tailStart = counters.Total - counters.ConsecutiveFailures;
        bool touchedTail = counters.ConsecutiveFailures > 0 && 1 > tailStart;
        LogEntry oldest = live[0];
        live.RemoveAt(0);
        counters.Remove(oldest);
        if (touchedTail) counters.RecomputeTail(live);
        agreeWhileEvicting &= Agree(counters, live);
    }
    Ck("逐条淘汰最旧记录后仍与基准一致", agreeWhileEvicting);
    Ck("清空后计数归零", counters.Total == 0 && counters.CountFor("read") == 0 && counters.ConsecutiveFailures == 0);

    counters.Clear();
    counters.Rebuild(seq);
    Ck("Clear + Rebuild 能还原全部计数", Agree(counters, seq) && counters.Total == seq.Count);

    // 整段全失败的边界：这正是淘汰会命中末尾区间、需要重扫的情形
    var allFail = new LogCounters();
    var failLive = new List<LogEntry>();
    bool agreeAllFail = true;
    for (int i = 0; i < 20; i++) { var x = Entry(i, "TX", -10); allFail.Add(x); failLive.Add(x); agreeAllFail &= Agree(allFail, failLive); }
    agreeAllFail &= allFail.ConsecutiveFailures == 20;
    for (int i = 0; i < 10; i++)
    {
        int tailStart = allFail.Total - allFail.ConsecutiveFailures;
        bool touchedTail = allFail.ConsecutiveFailures > 0 && 1 > tailStart;
        LogEntry oldest = failLive[0];
        failLive.RemoveAt(0);
        allFail.Remove(oldest);
        if (touchedTail) allFail.RecomputeTail(failLive);
        agreeAllFail &= Agree(allFail, failLive);
    }
    Ck("全失败日志逐条淘汰仍一致", agreeAllFail && allFail.ConsecutiveFailures == 10,
        $"连续失败={allFail.ConsecutiveFailures} 期望 10");

    return ckFailures == 0 ? 0 : 1;
}

// ── Aardvark 探针：--aardvark [bitrateKHz] ──
// 验证 aardvark.dll 可用性、枚举与打开链路。
// 运行期 DLL 来源：安装 Total Phase 驱动包（会部署到系统）或在应用目录放置官方完整版 aardvark.dll。
// 注意：SDK zip 内 c/aardvark.dll 是编译期导入库（节内容为空），不能作为运行时 DLL 分发。
if (args.Length > 0 && args[0] == "--aardvark")
{
    int khz = args.Length > 1 ? int.Parse(args[1]) : 400;
    int avFailures = 0;
    void AvCheck(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
        if (!ok) avFailures++;
    }

    // DLL 解析：优先应用目录，其次系统目录（驱动安装位置）
    string appDir = Path.Combine(AppContext.BaseDirectory, "aardvark.dll");
    string sysDir = Path.Combine(Environment.SystemDirectory, "aardvark.dll");
    string avDll = File.Exists(appDir) ? appDir : sysDir;
    if (!File.Exists(avDll))
    {
        Console.WriteLine("FAIL  未找到 aardvark.dll");
        Console.WriteLine("INFO  请安装 Total Phase Aardvark 驱动包（会部署系统级 aardvark.dll），");
        Console.WriteLine("      或将官方完整版 aardvark.dll（x64）放到应用目录。SDK zip 里的 c/aardvark.dll 是导入库，不可用。");
        return 1;
    }
    AvCheck("aardvark.dll 存在", true, avDll);
    AvCheck("aardvark.dll 可加载", NativeLibrary.TryLoad(avDll, out nint ah));
    if (ah != 0) NativeLibrary.Free(ah);

    int[] ports;
    try { ports = AardvarkI2c.FindDevices(); }
    catch (EntryPointNotFoundException)
    {
        Console.WriteLine("FAIL  DLL 缺少 aa_* 导出：该文件是导入库占位，不是运行时 DLL");
        return 1;
    }
    AvCheck("aa_find_devices 可调用", ports.Length >= 0, $"在线端口=[{string.Join(", ", ports)}]");
    if (ports.Length == 0)
    {
        Console.WriteLine("INFO  无在线 Aardvark，枚举链路验证通过");
        return avFailures == 0 ? 0 : 1;
    }

    int handle = AardvarkI2c.Open(ports[0], khz);
    AvCheck("aa_open + I²C 配置", handle >= 0, $"handle={handle} ({AardvarkI2c.ErrorName(handle)})");
    if (handle < 0) return 1;

    int fret = AardvarkI2c.FreeBus(handle);
    AvCheck("aa_i2c_free_bus", fret == AardvarkI2c.AA_OK, $"ret={fret} ({AardvarkI2c.ErrorName(fret)})");

    // 总线扫描：0x08-0x77 单字节原始读，NACK 即无设备
    var hits = new List<byte>();
    for (byte a = 0x08; a <= 0x77; a++)
    {
        var buf = new byte[1];
        if (AardvarkI2c.Read(handle, a, buf) == 1) hits.Add(a);
    }
    Console.WriteLine($"INFO  总线扫描命中 {(hits.Count == 0 ? "0 个（总线空闲）" : string.Join(" ", hits.Select(x => $"0x{x:X2}")))}");

    int cret = AardvarkI2c.Close(handle);
    AvCheck("aa_close", cret == AardvarkI2c.AA_OK, $"ret={cret}");
    return avFailures == 0 ? 0 : 1;
}

// ── BoardInfo 探针：--boardinfo ──
// 验证 VII_ReadBoardInfo 签名与序列号/固件读取（独立进程，无 GUI 会话冲突）。
if (args.Length > 0 && args[0] == "--boardinfo")
{
    if (GinkgoDriver.VII_ScanDevice(1) <= 0) { Console.WriteLine("FAIL 未检测到适配器"); return 1; }
    int oret = GinkgoDriver.VII_OpenDevice(GinkgoDriver.VII_USBI2C, 0, 0);
    Console.WriteLine($"open ret={oret}");

    var info = new GinkgoDriver.VII_BOARD_INFO
    {
        ProductName = new byte[32],
        FirmwareVersion = new byte[4],
        HardwareVersion = new byte[4],
        SerialNumber = new byte[12]
    };
    // 姿势 A：三参（DevType, DevIndex, ref）
    int retA = GinkgoDriver.VII_ReadBoardInfo(GinkgoDriver.VII_USBI2C, 0, ref info);
    Console.WriteLine($"三参 ReadBoardInfo ret={retA}: product={GinkgoDriver.Ascii(info.ProductName)} fw=v{info.FirmwareVersion[0]}.{info.FirmwareVersion[1]} sn={GinkgoDriver.Ascii(info.SerialNumber)} / hex={GinkgoDriver.Hex(info.SerialNumber)}");

    // 姿势 B：两参（DevIndex, ref）——UART 族签名
    var info2 = new GinkgoDriver.VII_BOARD_INFO
    {
        ProductName = new byte[32],
        FirmwareVersion = new byte[4],
        HardwareVersion = new byte[4],
        SerialNumber = new byte[12]
    };
    int retB = GinkgoDriver.ReadBoardInfo(0, ref info2);
    Console.WriteLine($"ReadBoardInfo(自动适配) ret={retB}: product={GinkgoDriver.Ascii(info2.ProductName)} fw=v{info2.FirmwareVersion[0]}.{info2.FirmwareVersion[1]} sn={GinkgoDriver.Ascii(info2.SerialNumber)} / hex={GinkgoDriver.Hex(info2.SerialNumber)}");
    return 0;
}

// ── 探针模式：dotnet run --project tests/SmokeTest -- --probe <7位地址hex> [reg] [len] [期望值hex] ──
// 绕过 GUI 直接验证总线读写，支持在两种速率下各扫一遍对比。
if (args.Length > 0 && args[0] == "--probe")
{
    byte addr7 = args.Length > 1 ? Convert.ToByte(args[1].Replace("0x", "").Replace("0X", ""), 16) : (byte)0x49;
    byte preg = args.Length > 2 ? Convert.ToByte(args[2].Replace("0x", "").Replace("0X", ""), 16) : (byte)0x00;
    int plen = args.Length > 3 ? int.Parse(args[3]) : 1;
    byte? expect = args.Length > 4 ? Convert.ToByte(args[4].Replace("0x", "").Replace("0X", ""), 16) : null;
    int pch = args.Length > 5 ? int.Parse(args[5]) : 0;

    Console.WriteLine($"探针：7位 0x{addr7:X2}（8位 0x{addr7 * 2:X2}/0x{addr7 * 2 + 1:X2}） reg=0x{preg:X2} len={plen} 期望={(expect.HasValue ? $"0x{expect:X2}" : "—")}");
    if (GinkgoDriver.VII_ScanDevice(1) <= 0) { Console.WriteLine("FAIL 未检测到适配器"); return 1; }
    int oret = GinkgoDriver.VII_OpenDevice(GinkgoDriver.VII_USBI2C, 0, 0);
    if (oret != 0) { Console.WriteLine($"FAIL 打开适配器失败：{GinkgoDriver.ErrorName(oret)}（GUI 若已连接请先断开）"); return 1; }

    foreach (var ch in new[] { 0, 1 })
    foreach (var khz in new[] { 100, 400 })
    {
        int iret = InitI2c((uint)(khz * 1000), GinkgoDriver.VII_SUB_ADDR_NONE, ch);
        var hits = new List<byte>();
        for (byte a = 0x08; a <= 0x77; a++)
        {
            var b = new byte[1];
            if (GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, ch, (ushort)(a << 1), 0, b, 1) == 0)
                hits.Add(a);
        }
        Console.WriteLine($"通道 {ch} @ {khz} kHz（InitI2C ret={iret}）：{(hits.Count == 0 ? "无命中" : "命中 " + string.Join(" ", hits.Select(x => $"0x{x:X2}")))}");
    }

    // 对照组：目标地址 vs 空地址，错误码不同则可区分“从机无 ACK”和“总线异常”
    _ = InitI2c(100_000, GinkgoDriver.VII_SUB_ADDR_NONE);
    foreach (var a in new byte[] { addr7, 0x12, 0x50 })
    {
        var b = new byte[1];
        int r = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, 0, (ushort)(a << 1), 0, b, 1);
        Console.WriteLine($"探测 0x{a:X2}（8位 0x{a * 2:X2}）：ret={r} ({GinkgoDriver.ErrorName(r)}){(r == 0 ? $" 数据 0x{b[0]:X2}" : "")}");
    }

    _ = InitI2c(100_000, GinkgoDriver.VII_SUB_ADDR_1BYTE, pch);
    var buf = new byte[plen];
    int pret = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, pch, (ushort)(addr7 << 1), preg, buf, (ushort)plen);
    Console.WriteLine(pret == 0
        ? $"寄存器读 @通道{pch} 100kHz：0x{Convert.ToHexString(buf)}{(expect.HasValue ? (buf[0] == expect.Value ? "  == 期望值 ✓" : $"  ≠ 期望值 0x{expect.Value:X2} ✗") : "")}"
        : $"寄存器读 @通道{pch} 100kHz 失败：{GinkgoDriver.ErrorName(pret)}");

    _ = InitI2c(100_000, GinkgoDriver.VII_SUB_ADDR_NONE, pch);
    var b2 = new byte[1];
    int pr2 = GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, pch, (ushort)(addr7 << 1), 0, b2, 1);
    Console.WriteLine(pr2 == 0 ? $"原始读（无子地址）@通道{pch} 100kHz：0x{b2[0]:X2}" : $"原始读失败：{GinkgoDriver.ErrorName(pr2)}");

    _ = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0);
    return 0;

    int InitI2c(uint hz, byte subW, int ch = 0)
    {
        var c = new GinkgoDriver.VII_INIT_CONFIG
        {
            MasterMode = GinkgoDriver.VII_MASTER,
            ControlMode = GinkgoDriver.VII_HCTL_MODE,
            AddrType = GinkgoDriver.VII_ADDR_7BIT,
            SubAddrWidth = subW,
            Addr = 0,
            ClockSpeed = hz
        };
        return GinkgoDriver.VII_InitI2C(GinkgoDriver.VII_USBI2C, 0, 0, ref c);
    }
}

// ── 周期读探针：--periodic <7位地址> <reg> <len> <次数> <间隔ms> [通道] ──
// 验证 I2cService.PeriodicReadAsync 的计时、取消与值回报。
if (args.Length > 0 && args[0] == "--periodic")
{
    byte paddr = Convert.ToByte(args[1].Replace("0x", ""), 16);
    byte preg = args.Length > 2 ? Convert.ToByte(args[2].Replace("0x", ""), 16) : (byte)0;
    int plen = args.Length > 3 ? int.Parse(args[3]) : 1;
    int ptimes = args.Length > 4 ? int.Parse(args[4]) : 5;
    int pms = args.Length > 5 ? int.Parse(args[5]) : 100;
    int pch = args.Length > 6 ? int.Parse(args[6]) : 0;

    var svc = new GinkgoHost.Services.I2cService();
    var (cnt, oret) = await svc.ConnectAsync(pch, 100_000, GinkgoDriver.VII_HCTL_MODE);
    if (cnt <= 0 || oret != 0) { Console.WriteLine($"FAIL 适配器打开失败（{cnt}/{oret}）"); return 1; }

    int n = 0;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int done = 0; string? reason = null;
    try
    {
        (done, reason) = await svc.PeriodicReadAsync(pms, paddr, preg, plen, r =>
        {
            var nn = Interlocked.Increment(ref n);
            Console.WriteLine($"  #{nn} [{r.Ms:F1} ms] {(r.Ok ? $"0x{Convert.ToHexString(r.Data!)}" : GinkgoDriver.ErrorName(r.Ret))}");
            if (nn >= ptimes) cts.Cancel(); // 达到目标次数即停
        }, cts.Token);
    }
    catch (OperationCanceledException) { /* 达到次数主动取消，正常路径 */ }
    sw.Stop();
    await svc.CloseAsync();
    Console.WriteLine($"完成 {done} 次，实际耗时 {sw.ElapsedMilliseconds} ms{(reason is null ? "" : $"，停止原因：{reason}")}");
    Console.WriteLine(done >= ptimes ? $"PASS 周期读（{done} 次全部完成）" : "FAIL 周期读未达目标次数");
    return 0;
}

// ── 子地址读探针：--subread <7位地址> <子地址hex> <len> <宽度1|2> [通道] ──
// 验证扩展页使用的 ReadSubAddrAsync 服务路径。
if (args.Length > 0 && args[0] == "--subread")
{
    byte saddr = Convert.ToByte(args[1].Replace("0x", ""), 16);
    uint ssub = Convert.ToUInt32(args[2].Replace("0x", ""), 16);
    int slen = args.Length > 3 ? int.Parse(args[3]) : 1;
    byte sw = args.Length > 4 ? byte.Parse(args[4]) : (byte)1;
    int sch = args.Length > 5 ? int.Parse(args[5]) : 0;

    var svc = new GinkgoHost.Services.I2cService();
    var (cnt, oret) = await svc.ConnectAsync(sch, 100_000, GinkgoDriver.VII_HCTL_MODE);
    if (cnt <= 0 || oret != 0) { Console.WriteLine($"FAIL 适配器打开失败（{cnt}/{oret}）"); return 1; }
    var r = await svc.ReadSubAddrAsync(saddr, ssub, slen, sw);
    await svc.CloseAsync();
    Console.WriteLine(r.Ok
        ? $"PASS 子地址读 0x{saddr:X2}[0x{ssub:X4}] x{slen} = 0x{Convert.ToHexString(r.Data!)}（{r.Ms:F1} ms）"
        : $"FAIL 子地址读失败：{GinkgoDriver.ErrorName(r.Ret)}");
    return r.Ok ? 0 : 1;
}

// ── 常规冒烟 ──
int failures = 0;

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
    if (!ok) failures++;
}

// 1. DLL 文件在输出目录
string dllPath = Path.Combine(AppContext.BaseDirectory, "Ginkgo_Driver.dll");
Check("DLL 随构建输出", File.Exists(dllPath), dllPath);

// 2. DLL 位宽 x64（进程 x64，必须匹配）
if (File.Exists(dllPath))
{
    var b = File.ReadAllBytes(dllPath);
    int pe = BitConverter.ToInt32(b, 0x3C);
    ushort machine = BitConverter.ToUInt16(b, pe + 4);
    Check("DLL 架构 x64", machine == 0x8664, $"machine=0x{machine:X4}");
}

// 3. 库可加载。干净机器（如 CI）未装 ViewTool 驱动运行时，Ginkgo_Driver.dll 的
//    依赖缺失属预期：原生调用段整体 SKIP（不计失败），纯托管检查继续。
bool nativeOk = NativeLibrary.TryLoad(dllPath, out nint h);
if (nativeOk) Check("NativeLibrary 加载", true);
else Console.WriteLine("SKIP  NativeLibrary 加载（依赖缺失，未装驱动运行时）");
if (h != 0) NativeLibrary.Free(h);
Check("设备标识过滤控制字符",
    GinkgoDriver.Ascii([(byte)'S', (byte)'U', (byte)'1', (byte)'4', (byte)'8', (byte)'6', 0x07, 0]) == "SU1486");
Check("ASCII 预览保留可读字节",
    I2cPage.AsciiPreview([(byte)'A', 0x00, (byte)'~']) == "A·~");
Check("ASCII 预览截断长数据",
    I2cPage.AsciiPreview(Enumerable.Repeat((byte)'A', 97).ToArray()) is { Length: 97 } preview && preview.EndsWith('…'));
Check("读取变化摘要定位偏移",
    I2cPage.DescribeReadDelta([0x00, 0x01, 0x02, 0x03], [0x00, 0x11, 0x02, 0x13]) == "变化 2 B · 0x01、0x03");
Check("读取变化摘要识别相同数据",
    I2cPage.DescribeReadDelta([0x00, 0x01], [0x00, 0x01]) == "与上次读取相同");

// 4-5. 入口点可调用：无适配器时返回 0（个数）/错误码，证明调用约定正确
if (nativeOk)
{
    int count = GinkgoDriver.VII_ScanDevice(1);
    Check("VII_ScanDevice 可调用", count >= 0, $"适配器数={count}");

    int ret = GinkgoDriver.VII_OpenDevice(GinkgoDriver.VII_USBI2C, 0, 0);
    Check("VII_OpenDevice 错误处理", count > 0 || ret != 0, $"ret={ret} ({GinkgoDriver.ErrorName(ret)})");

    // 6. 在线阶段：适配器在位时做一次真初始化 + 总线扫描，验证完整链路
    if (count > 0 && ret == 0)
{
    // 诊断：open 后留整流时间再 init；失败则断开重连重试
    Thread.Sleep(300);
    int iret = InitI2C100k();
    for (int attempt = 2; iret != 0 && attempt <= 4; attempt++)
    {
        Console.WriteLine($"INFO  InitI2C 第 {attempt - 1} 次失败 ({GinkgoDriver.ErrorName(iret)})，重连重试…");
        _ = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0);
        Thread.Sleep(500);
        _ = GinkgoDriver.VII_OpenDevice(GinkgoDriver.VII_USBI2C, 0, 0);
        Thread.Sleep(300);
        iret = InitI2C100k();
    }
    Check("VII_InitI2C 在线初始化", iret == 0, $"ret={iret} ({GinkgoDriver.ErrorName(iret)})");

    var hits = new List<byte>();
    for (byte a = 0x08; a <= 0x77; a++)
    {
        var buf = new byte[1];
        if (GinkgoDriver.VII_ReadBytes(GinkgoDriver.VII_USBI2C, 0, 0, (ushort)(a << 1), 0, buf, 1) == 0)
            hits.Add(a);
    }
    Console.WriteLine($"INFO  总线扫描命中 {(hits.Count == 0 ? "0 个（总线空闲）" : string.Join(" ", hits.Select(x => $"0x{x:X2}")))}");
    _ = GinkgoDriver.VII_CloseDevice(GinkgoDriver.VII_USBI2C, 0);

    // 服务层连接必须在同一串行链路内生成身份快照，页面不应再直接调用驱动读取 BoardInfo。
    using var serviceSmoke = new I2cService();
    var (serviceCount, serviceRet) = await serviceSmoke.ConnectAsync(0, 100_000, GinkgoDriver.VII_HCTL_MODE);
    Check("I2cService 在线连接", serviceCount > 0 && serviceRet == 0,
        $"count={serviceCount}, ret={serviceRet} ({GinkgoDriver.ErrorName(serviceRet)})");
    Check("I2cService 设备身份快照", serviceSmoke.AdapterInfo is not null,
        serviceSmoke.AdapterInfo is { } identity
            ? $"sn={identity.SerialNumber}, fw={identity.FirmwareVersion}"
            : "未取得身份信息");
    int serviceCloseRet = await serviceSmoke.CloseAsync();
    Check("I2cService 在线关闭", serviceCloseRet == 0,
        $"ret={serviceCloseRet} ({GinkgoDriver.ErrorName(serviceCloseRet)})");
    int refreshedCount = await serviceSmoke.ScanAdaptersAsync();
    Check("I2cService 仅刷新适配器资源", refreshedCount == count,
        $"count={refreshedCount}");
    Check("I2cService 刷新清除旧身份快照", serviceSmoke.AdapterInfo is null,
        serviceSmoke.AdapterInfo is null ? "已清除" : "仍保留旧身份");
    }
}
else
{
    Console.WriteLine("SKIP  原生调用段（Ginkgo_Driver.dll 依赖缺失，纯托管检查继续）");
}

static int InitI2C100k()
{
    var cfg = new GinkgoDriver.VII_INIT_CONFIG
    {
        MasterMode = GinkgoDriver.VII_MASTER,
        ControlMode = GinkgoDriver.VII_HCTL_MODE,
        AddrType = GinkgoDriver.VII_ADDR_7BIT,
        SubAddrWidth = GinkgoDriver.VII_SUB_ADDR_NONE,
        Addr = 0,
        ClockSpeed = 100_000
    };
    return GinkgoDriver.VII_InitI2C(GinkgoDriver.VII_USBI2C, 0, 0, ref cfg);
}

// 7. 控制台命令解析器自检（纯函数，无硬件依赖）
Check("解析 read 50 8",
    CommandParser.Parse("read 50 8").Cmd is ReadCmd r1 && r1.Addr == 0x50 && r1.Reg == null && r1.Len == 8);
Check("解析 read 50 00 8",
    CommandParser.Parse("read 50 00 8").Cmd is ReadCmd r2 && r2.Reg == 0x00 && r2.Len == 8);
Check("解析 write 50 00 de ad be ef",
    CommandParser.Parse("write 50 00 de ad be ef").Cmd is WriteCmd w1 && w1.Reg == 0 && w1.Data.Length == 4 && w1.Data[0] == 0xDE);
Check("解析 write 50 aa",
    CommandParser.Parse("write 50 aa").Cmd is WriteCmd w2 && w2.Reg == null && w2.Data[0] == 0xAA);
Check("解析 speed 400",
    CommandParser.Parse("speed 400").Cmd is SpeedCmd s1 && s1.KHz == 400);
Check("未知命令报错", CommandParser.Parse("bogus").Error != null);
Check("超长读拒绝", CommandParser.Parse("read 50 999").Error != null);
Check("解析 sleep 200", CommandParser.Parse("sleep 200").Cmd is SleepCmd sl && sl.Ms == 200);
Check("sleep 零值拒绝", CommandParser.Parse("sleep 0").Error != null);
Check("解析 run init.txt", CommandParser.Parse("run init.txt").Cmd is RunScriptCmd run1 && run1.Path == "init.txt");
Check("解析带空格路径", CommandParser.Parse("run C:\\my dir\\s.txt").Cmd is RunScriptCmd run2 && run2.Path == "C:\\my dir\\s.txt");
Check("run 缺参数报错", CommandParser.Parse("run").Error != null);
Check("Profile 名称边界",
    ProfileService.IsValidName("sensor-v2") && !ProfileService.IsValidName("..") &&
    !ProfileService.IsValidName("sensor/profile"));

// 8. 服务边界自检：必须在触及驱动或分配读取缓冲区前拒绝非法参数。
using (var validationSvc = new I2cService())
{
    Check("服务拒绝 8-bit 从机地址",
        await ThrowsAsync<ArgumentOutOfRangeException>(() => validationSvc.RawReadAsync(0x80, 1)));
    Check("服务拒绝零长度读取",
        await ThrowsAsync<ArgumentOutOfRangeException>(() => validationSvc.RawReadAsync(0x50, 0)));
    Check("服务拒绝非法子地址宽度",
        await ThrowsAsync<ArgumentOutOfRangeException>(() => validationSvc.ReadSubAddrAsync(0x50, 0, 1, 0)));
    Check("服务拒绝硬件模式的越界通道",
        await ThrowsAsync<ArgumentOutOfRangeException>(() => validationSvc.ApplyConfigAsync(2, 100_000, GinkgoDriver.VII_HCTL_MODE)));
}

Console.WriteLine(failures == 0 ? "\n全部通过" : $"\n{failures} 项失败");
return failures == 0 ? 0 : 1;

static async Task<bool> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
        return false;
    }
    catch (TException) { return true; }
}
