using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace PrinterIpFix
{
    internal static class MatchProbe
    {
        private static void W(string s) { Console.WriteLine(s); }

        public static int Run(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // 1) 队列 + 端口 + 分类 + 解析
            Dictionary<string, PortInfo> ports = PrinterSystem.GetTcpPorts();
            List<PrinterInfo> printers = PrinterSystem.GetPrinters();
            foreach (PrinterInfo p in printers)
            {
                PrinterSystem.Classify(p, ports);
                p.ResolvedIp = PrinterSystem.ResolveAddress(p.RawAddress);
            }

            W("=== 队列 (" + printers.Count + ") ===");
            foreach (PrinterInfo p in printers)
            {
                W(string.Format("  {0,-32} kind={1,-7} port={2,-30} raw={3,-22} ip={4,-15} fixable={5,-5} default={6}",
                    p.Name, p.KindText, p.PortName, p.RawAddress, p.ResolvedIp, p.Fixable, p.IsDefault));
            }

            // 1b) 端口字典直查（定位 RawAddress 为空的真因）
            W("");
            W("=== GetTcpPorts() 字典 (" + ports.Count + ") ===");
            foreach (KeyValuePair<string, PortInfo> kv in ports)
            {
                W(string.Format("  key=[{0}] len={1} host=[{2}] port={3} protocol={4} snmp={5}",
                    kv.Key, kv.Key.Length, kv.Value.HostAddress, kv.Value.PortNumber,
                    kv.Value.Protocol, kv.Value.SnmpEnabled));
            }
            foreach (PrinterInfo dbg in printers)
            {
                if (dbg.Kind != PortKind.TcpIp) continue;
                string pn = dbg.PortName == null ? "" : dbg.PortName;
                PortInfo hit;
                bool ok2 = ports.TryGetValue(pn, out hit);
                W(string.Format("  队列[{0}] portname=[{1}] len={2} 字典命中={3} 命中host=[{4}]",
                    dbg.Name, pn, pn.Length, ok2,
                    ok2 ? hit.HostAddress : "<无>")); 
            }

            // 2) 探测端口已知地址（真实 EnsureAlive）
            W("");
            W("=== EnsureAlive 探测（端口当前地址） ===");
            var sw = new System.Diagnostics.Stopwatch();
            foreach (PrinterInfo p in printers)
            {
                if (p.Kind != PortKind.TcpIp) continue;
                if (string.IsNullOrEmpty(p.ResolvedIp)) { p.AddressAlive = false; p.PingAlive = false; W("  " + p.Name + " -> 端口地址无法解析"); continue; }
                sw.Reset(); sw.Start();
                bool ping;
                bool ok = Scanner.EnsureAlive(p.ResolvedIp, out ping);
                sw.Stop();
                p.AddressAlive = ok;
                p.PingAlive = ping;
                W(string.Format("  {0,-30} {1,-15} alive={2,-5} ping={3,-5} {4}ms",
                    p.Name, p.ResolvedIp, ok, ping, sw.ElapsedMilliseconds));
            }

            // 3) 扫描：只扫"候选地址集"，不扫整个 /24
            //    候选 = 命令行显式给出的地址 + 各 TCP 队列端口解析出的地址
            List<IPAddress> targets = new List<IPAddress>();
            var seen = new Dictionary<string, bool>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--")) continue;
                IPAddress ipa = null;
                try { ipa = IPAddress.Parse(a); } catch (Exception) { ipa = null; }
                if (ipa == null) { W("  (跳过无法解析的入参: " + a + ")"); continue; }
                string key = ipa.ToString();
                if (seen.ContainsKey(key)) continue;
                seen[key] = true; targets.Add(ipa);
            }
            foreach (PrinterInfo p in printers)
            {
                if (p.Kind != PortKind.TcpIp || string.IsNullOrEmpty(p.ResolvedIp)) continue;
                string key = p.ResolvedIp;
                if (seen.ContainsKey(key)) continue;
                IPAddress ipa = null;
                try { ipa = IPAddress.Parse(p.ResolvedIp); } catch (Exception) { ipa = null; }
                if (ipa == null) continue;
                seen[key] = true; targets.Add(ipa);
            }

            W("");
            W("=== Sweep (" + targets.Count + " 个地址) ===");
            foreach (IPAddress t in targets) W("  目标 " + t);

            List<DeviceInfo> devices = new List<DeviceInfo>();
            sw.Reset(); sw.Start();
            Scanner.Sweep(targets,
                delegate(DeviceInfo d) { lock (devices) { devices.Add(d); } },
                null,
                null,
                400,
                900);
            sw.Stop();
            W("  发现 " + devices.Count + " 台，耗时 " + sw.ElapsedMilliseconds + "ms");
            foreach (DeviceInfo d in devices)
            {
                W(string.Format("  {0,-15} name={1,-32} model={2,-28} state={3,-3} reasons={4,-20} svc={5,-12} serial={6}",
                    d.Ip, d.IppName, d.Model, d.IppState, d.StateReasons, d.Services, d.Serial));
                if (!string.IsNullOrEmpty(d.Error)) W("      error=" + d.Error);
            }

            // 4) 匹配（复刻 MainForm.AutoMatch 的接受判据）
            W("");
            W("=== Matcher.Rank / AutoMatch 判据 ===");
            foreach (PrinterInfo p in printers)
            {
                if (!p.Fixable) { W("  " + p.Name + " -> (不可修，跳过匹配)"); continue; }
                List<MatchCandidate> ranked = Matcher.Rank(p, devices);
                if (ranked.Count == 0) { W("  " + p.Name + " -> 无候选（Score 全 0）"); continue; }
                W("  " + p.Name + " -> 候选 " + ranked.Count + " 个:");
                foreach (MatchCandidate c in ranked)
                {
                    W(string.Format("      {0,-15} score={1,3} same={2,-5} {3}",
                        c.Device.Ip, c.Score, c.SameAddress, c.Reason));
                }
                MatchCandidate best = ranked[0];
                bool unique = ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 15;
                bool accepted = best.Score >= 45 && unique;
                W(string.Format("      => {0}  best={1} unique={2} accepted={3}",
                    accepted ? ("采用 " + best.Device.Ip) : "(不采用)", best.Score, unique, accepted));
            }

            // 5) 状态列文案（FillPrinterList 逻辑）
            W("");
            W("=== 状态列文案 ===");
            foreach (PrinterInfo p in printers)
            {
                if (p.Kind != PortKind.TcpIp || p.AddressAlive != false) { W("  " + p.Name + " -> (正常/非TCP)"); continue; }
                string st = p.PingAlive == true ? "设备无应答" : "地址不通";
                W("  " + p.Name + " -> " + st);
            }

            // 6) 全局限定：匹配是否只靠 IP_ 端口
            W("");
            W("=== 端口类型汇总 ===");
            int wsdc = 0, tcp = 0, other = 0;
            foreach (PrinterInfo p in printers)
            {
                switch (p.Kind)
                {
                    case PortKind.Wsd: wsdc++; break;
                    case PortKind.TcpIp: tcp++; break;
                    default: other++; break;
                }
            }
            W(string.Format("  TCP/IP={0}  WSD={1}  其它={2}", tcp, wsdc, other));
            return 0;
        }
    }
}
