using System.Globalization;

namespace GinkgoHost.Services;

/// <summary>控制台命令对象。</summary>
public abstract record Cmd;
public sealed record ConnectCmd : Cmd;
public sealed record DisconnectCmd : Cmd;
public sealed record SpeedCmd(uint KHz) : Cmd;
public sealed record ScanAdapterCmd : Cmd;
public sealed record ScanBusCmd : Cmd;
public sealed record ClearCmd : Cmd;
public sealed record HelpCmd : Cmd;
public sealed record StatusCmd : Cmd;
public sealed record ConfigCmd : Cmd;
/// <summary>read 50 8 | read 50 00 8（Addr 7 位 hex，Reg 可选 hex，Len 十进制 1–256）。</summary>
public sealed record ReadCmd(byte Addr, byte? Reg, int Len) : Cmd;
/// <summary>write 50 de ad | write 50 00 de ad（Reg 可选）。</summary>
public sealed record WriteCmd(byte Addr, byte? Reg, byte[] Data) : Cmd;
/// <summary>readloop 50 8 500 | readloop 50 00 8 500（最后一项是周期 ms）。</summary>
public sealed record ReadLoopCmd(byte Addr, byte? Reg, int Len, int PeriodMs) : Cmd;
public sealed record StopCmd : Cmd;
/// <summary>sleep <ms>：延时，脚本编排用。</summary>
public sealed record SleepCmd(int Ms) : Cmd;
/// <summary>run <文件>：按行执行脚本；# 与空行跳过，出错即停。</summary>
public sealed record RunScriptCmd(string Path) : Cmd;

/// <summary>
/// 控制台命令解析（纯函数，可独立测试）。
/// 约定：地址/寄存器为 hex（可带 0x 前缀），长度为十进制；read/write
/// 第 3 个 token 若后随长度/数据则视为可选寄存器号。
/// </summary>
public static class CommandParser
{
    public static (Cmd? Cmd, string? Error) Parse(string line)
    {
        var t = line.Trim().Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0) return (null, "空命令，输入 help 查看用法");
        try
        {
            return t[0].ToLowerInvariant() switch
            {
                "help" or "?" => (new HelpCmd(), null),
                "clear" or "cls" => (new ClearCmd(), null),
                "status" => (new StatusCmd(), null),
                "config" => (new ConfigCmd(), null),
                "connect" => (new ConnectCmd(), null),
                "disconnect" => (new DisconnectCmd(), null),
                "scan" => (new ScanBusCmd(), null),
                "adapters" => (new ScanAdapterCmd(), null),
                "speed" => ParseSpeed(t),
                "read" => ParseRead(t),
                "write" => ParseWrite(t),
                "readloop" or "rloop" => ParseReadLoop(t),
                "stop" => (new StopCmd(), null),
                "sleep" => ParseSleep(t),
                "run" => ParseRun(t),
                _ => (null, $"未知命令 “{t[0]}”，输入 help 查看用法")
            };
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static (Cmd?, string?) ParseSpeed(string[] t)
    {
        if (t.Length != 2) return (null, "用法：speed <kHz>，如 speed 400");
        uint khz = uint.Parse(t[1], CultureInfo.InvariantCulture); // 格式错误抛出，统一走 error
        if (khz is < 1 or > 2000) return (null, "速率须在 1–2000 kHz 之间");
        return (new SpeedCmd(khz), null);
    }

    private static byte HexByte(string s)
    {
        s = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return byte.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static (Cmd?, string?) ParseRead(string[] t)
    {
        if (t.Length is < 2 or > 4) return (null, "用法：read <addr> [reg] <len>");
        byte addr = HexByte(t[1]);
        int len = int.Parse(t[^1], CultureInfo.InvariantCulture);
        if (len is < 1 or > 256) return (null, "长度须在 1–256 之间");
        return t.Length == 4
            ? (new ReadCmd(addr, HexByte(t[2]), len), null)
            : (new ReadCmd(addr, null, len), null);
    }

    private static (Cmd?, string?) ParseWrite(string[] t)
    {
        if (t.Length is < 3 or > 18) return (null, "用法：write <addr> [reg] <b1 b2 ...>，最多 16 字节");
        byte addr = HexByte(t[1]);
        byte[] data = t.Skip(t.Length == 3 ? 2 : 3).Select(HexByte).ToArray();
        if (data.Length == 0) return (null, "写入数据为空");
        return t.Length > 3
            ? (new WriteCmd(addr, HexByte(t[2]), data), null)
            : (new WriteCmd(addr, null, data), null);
    }

    private static (Cmd?, string?) ParseReadLoop(string[] t)
    {
        if (t.Length is not 4 and not 5)
            return (null, "用法：readloop <addr> [reg] <len> <ms>");
        byte addr = HexByte(t[1]);
        int len = int.Parse(t[^2], CultureInfo.InvariantCulture);
        int periodMs = int.Parse(t[^1], CultureInfo.InvariantCulture);
        if (len is < 1 or > 256) return (null, "长度须在 1–256 之间");
        if (periodMs is < 50 or > 60_000) return (null, "周期须在 50–60000 ms 之间");
        return t.Length == 5
            ? (new ReadLoopCmd(addr, HexByte(t[2]), len, periodMs), null)
            : (new ReadLoopCmd(addr, null, len, periodMs), null);
    }

    private static (Cmd?, string?) ParseSleep(string[] t)
    {
        if (t.Length != 2) return (null, "用法：sleep <ms>，如 sleep 200");
        int ms = int.Parse(t[1], CultureInfo.InvariantCulture);
        if (ms is < 1 or > 600_000) return (null, "延时须在 1–600000 ms 之间");
        return (new SleepCmd(ms), null);
    }

    private static (Cmd?, string?) ParseRun(string[] t)
    {
        if (t.Length < 2) return (null, "用法：run <文件>，如 run init.txt（脚本内 # 开头为注释）");
        // 路径可含空格：把首 token 之外的全部拼回去，兼容带引号写法
        string path = string.Join(" ", t.Skip(1)).Trim().Trim('"');
        if (path.Length == 0) return (null, "用法：run <文件>，如 run init.txt（脚本内 # 开头为注释）");
        return (new RunScriptCmd(path), null);
    }
}
