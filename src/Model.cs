using System;
using System.Collections.Generic;
using System.Net;

namespace PrinterIpFix
{
    /// <summary>端口类别</summary>
    internal enum PortKind
    {
        Unknown = 0,
        TcpIp = 1,      // 标准 TCP/IP 端口（RAW / LPR），有 HostAddress
        Wsd = 2,        // WSD 自动发现端口，地址是烘死的
        Usb = 3,        // USB 本地口
        Local = 4,      // 本地虚拟口（PORTPROMPT / 文件 / 传真）
        Smb = 5         // \\server\share 共享打印机
    }

    internal sealed class PortInfo
    {
        public string Name = "";
        public string HostAddress = "";
        public int PortNumber = 9100;
        public int Protocol = 1;   // 1 = RAW, 2 = LPR
        public bool SnmpEnabled;
        public bool IsTcp;         // 是否出现在 Win32_TCPIPPrinterPort 里
    }

    internal sealed class PrinterInfo
    {
        public string Name = "";
        public string PortName = "";
        public string DriverName = "";
        public bool WorkOffline;
        public bool IsDefault;
        public int StatusCode = 0;

        // 运行时填充
        public PortKind Kind = PortKind.Unknown;
        public string RawAddress = "";     // 端口里记的原始地址（可能是主机名）
        public string ResolvedIp = "";     // 解析后的 IPv4
        public bool? AddressAlive;         // 端口地址上是否真的有打印机
        public bool? PingAlive;            // 该地址能不能 ping 通（区分"没设备"与"设备在但深睡不应答"）
        public DeviceInfo Match;           // 自动匹配到的设备
        public string MatchReason = "";
        public int MatchScore = 0;
        public string Advice = "";
        public bool Fixable = true;

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case PortKind.TcpIp: return "TCP/IP";
                    case PortKind.Wsd: return "WSD";
                    case PortKind.Usb: return "USB";
                    case PortKind.Local: return "本地/虚拟";
                    case PortKind.Smb: return "共享";
                    default: return "未知";
                }
            }
        }

        public string StatusText
        {
            get
            {
                if (WorkOffline) return "脱机";
                switch (StatusCode)
                {
                    case 0: return "就绪";
                    case 1: return "其它";
                    case 2: return "未知";
                    case 3: return "空闲";
                    case 4: return "打印中";
                    case 5: return "预热";
                    case 6: return "停止打印";
                    case 7: return "脱机";
                    case 8: return "错误";
                    default: return "状态" + StatusCode;
                }
            }
        }
    }

    internal sealed class DeviceInfo
    {
        public string Ip = "";
        public string IppName = "";
        public string Model = "";
        public string Serial = "";
        public string Uuid = "";
        public string DeviceId = "";
        public string StateReasons = "";
        public int IppState = -1;
        public bool HasIpp;
        public bool Has9100;
        public bool HasLpd;
        public int RttMs = -1;
        public string Error = "";

        public string Services
        {
            get
            {
                var t = new List<string>();
                if (HasIpp) t.Add("IPP");
                if (Has9100) t.Add("9100");
                if (HasLpd) t.Add("LPD");
                return t.Count == 0 ? "-" : string.Join("+", t.ToArray());
            }
        }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(IppName)) return IppName;
                if (!string.IsNullOrEmpty(Model)) return Model;
                return Ip;
            }
        }

        /// <summary>设备身份的全部可比较文本</summary>
        public string IdentityText
        {
            get
            {
                return string.Join(" ", new string[] {
                    IppName, Model, DeviceId, Uuid, Ip, Serial
                });
            }
        }
    }

    internal sealed class FixResult
    {
        public string Printer = "";
        public bool Ok;
        public string Detail = "";
        public List<string> Steps = new List<string>();
    }

    /// <summary>插在 USB 上的打印设备，Port 是它占用的端口后缀（如 USB001）。</summary>
    internal sealed class UsbDevice
    {
        public string FriendlyName = "";
        public string InstanceId = "";
        public string Port = "";

        public string DisplayText
        {
            get
            {
                string name = FriendlyName.Length > 0 ? FriendlyName : "(未命名 USB 打印设备)";
                return Port.Length > 0 ? name + "  →  " + Port : name;
            }
        }
    }
}
