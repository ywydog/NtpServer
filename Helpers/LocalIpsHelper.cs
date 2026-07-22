using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NtpServer.Helpers;

/// <summary>本机网络信息辅助。</summary>
public static class LocalIpsHelper
{
    /// <summary>获取本机所有处于 Up 状态的 IPv4 物理网卡地址。</summary>
    public static IReadOnlyList<string> GetLocalIpv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                             && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork
                               && !IPAddress.IsLoopback(addr.Address)
                               && !addr.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(addr => addr.Address.ToString())
                .Distinct()
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
