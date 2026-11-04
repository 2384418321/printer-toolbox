# 无头诊断程序

`tools\` 下的两个文件用于在没有 GUI 的情况下验证整条匹配管线：

| 文件 | 作用 |
|---|---|
| `MatchProbe.cs` | 主逻辑：枚举队列 → 读端口 → 分类 → 解析地址 → 探活 → 网段扫描 → 打分匹配 → 输出界面状态列文案 |
| `ProbeMain.cs` | 入口（`Main`），只是把参数转给 `MatchProbe.Run` |

## 为什么不直接用 GUI

GUI 的点击动作无法在无头环境里可靠驱动（需要真实桌面会话）。这个探针复刻了 `MainForm.AutoMatch` 的完整判定逻辑，包括阈值：

```csharp
bool accepted = ranked[0].Score >= 45
             && (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= 15);
```

所以它输出「accepted=True/False」与界面匹配列显示什么是一一对应的。

## 编译

需要和主程序共用 `src\` 下的业务代码（不含 WinForms 部分）。在仓库根目录执行：

```powershell
New-Item -ItemType Directory -Path tools\bin -Force | Out-Null

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:exe /out:tools\bin\MatchProbe.exe /langversion:5 `
    /r:System.dll /r:System.Core.dll /r:System.Management.dll `
    src\Model.cs src\Matcher.cs src\PrinterSystem.cs src\Scanner.cs src\Ipp.cs `
    tools\ProbeMain.cs tools\MatchProbe.cs
```

> 必须先建 `tools\bin`，否则 csc 会报 `CS1567: 写入 Win32 资源时出错: 系统找不到指定的路径。`
>
> 编译时可能出现 2 条 `CS0649`（字段从未赋值）警告，那是 WinForms 侧才用到的字段，无害。

## 运行

```powershell
.\tools\bin\MatchProbe.exe 192.168.1.100 192.168.1.101
```

参数是你想额外扫描的地址（除了队列端口里已解析出的地址之外）。**不扫整个 /24**——那需要 510 次多探针超时，没必要。

## 输出说明

```
=== queues ===
  [Canon MF210 (TCP)]        kind=TCP/IP port=IP_192.168.1.101 raw=192.168.1.101 ip=192.168.1.101 fixable=True
  ...

=== port dictionary ===        <- 关键：端口键与实际读到的地址
  key=[IP_192.168.1.101] len=16 host=[192.168.1.101] port=9100 proto=1
  ...

=== liveness ===
  Canon MF210 (TCP) 192.168.1.101 alive=True ping=True 33ms

=== sweep ===
  192.168.1.101  name=MF210 model=CANON MF210 state=3 reasons=none serial=...
  ...

=== match ===
  Canon MF210 (TCP)  score=100 same=True accepted=True reason=...
  ...

=== status column text ===
  Canon MF210 (TCP)  -> (空，表示正常)
```

第 2 段「port dictionary」是最重要的诊断信息。如果这里的 `host=[]` 是空的，说明踩了 README 里写的那条 WMI 坑——PowerShell 读得到、C# 读不到。

## 注意

这个探针只读：它不建端口、不改队列、不发打印数据。唯一的副作用是会向目标地址发 TCP SYN 和 IPP 查询。
