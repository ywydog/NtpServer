using System.Security.Principal;

namespace NtpServer.Helpers;

/// <summary>
/// 与 Windows 管理员权限相关的辅助方法。
/// </summary>
public static class AdminHelper
{
    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsRunningInAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(id);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
