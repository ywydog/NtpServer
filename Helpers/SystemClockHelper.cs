using System.Runtime.InteropServices;

namespace NtpServer.Helpers;

/// <summary>
/// 修改本机时间的辅助类。提供两种方式：
/// 1) 软调整：累加 ClassIsland 的 TimeOffsetSeconds（无需管理员）
/// 2) 硬调整：调用 kernel32!SetSystemTime（需管理员）
/// </summary>
public static class SystemClockHelper
{
    /// <summary>
    /// 软调整 ClassIsland 显示时间：在 <paramref name="offsetSeconds"/> 上累加 <paramref name="deltaSeconds"/>。
    /// 不会真正修改系统时间，重启 ClassIsland 后归零。
    /// </summary>
    public static bool ApplySoftOffset(ref double offsetSeconds, double deltaSeconds, double? maxSeconds = null)
    {
        if (Math.Abs(deltaSeconds) < 0.001) return true;
        if (maxSeconds.HasValue && Math.Abs(offsetSeconds + deltaSeconds) > maxSeconds.Value)
        {
            return false;
        }
        offsetSeconds = Math.Round(offsetSeconds + deltaSeconds, 3);
        return true;
    }

    /// <summary>
    /// 硬调整 Windows 系统时间。需以管理员身份运行。
    /// </summary>
    public static bool ApplyHardTime(DateTime localTime, out string? error)
    {
        error = null;
        try
        {
            var utc = localTime.ToUniversalTime();
            var st = new SystemTime
            {
                Year = (ushort)utc.Year,
                Month = (ushort)utc.Month,
                Day = (ushort)utc.Day,
                Hour = (ushort)utc.Hour,
                Minute = (ushort)utc.Minute,
                Second = (ushort)utc.Second,
                Milliseconds = (ushort)utc.Millisecond
            };
            var ok = SetSystemTime(ref st);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                error = $"SetSystemTime 失败，错误码: {err}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetSystemTime(ref SystemTime st);
}
