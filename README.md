# PrinterToolbox · 打印机工具箱

> 一个约 243 KB 的单文件 Windows 工具，专门解决「打印机换了 IP，队列里的活全卡着不出纸」这一类问题。

单文件、免安装、不需要 .NET SDK、不收集任何数据。双击即用。默认完全离线（只在本网段发 TCP/ping 探测、只读本机注册表和 WMI）；只有你在「驱动修复」里主动点搜索/下载时才会访问驱动站与厂商站点。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue)]()

---

## 它解决什么问题

打印机 DHCP 换了个地址，Windows 打印队列还指着旧 IP。表现是：

- WPS / Word 点打印，弹「**无法启动打印作业，请检查打印机是否开机并与计算机联机**」
- 或者作业显示已发送，但打印机面板上永远「打印中」，就是不出纸
- 或者重启电脑、重启打印服务，能好一次，下次又坏

Windows 自带的「疑难解答」修不好这类问题，因为它不知道队列该指向哪个地址。这个工具知道——它去网络上问每台打印机「你是谁」（IPP 协议），拿到型号、序列号、UUID，再跟本机队列的身份比对，配上了就把端口指针改到设备当前地址。

---

## 四个页签

### ① 校准 / 队列

本机打印队列（左）和网段里扫到的打印设备（右）两栏并排。

- **自动匹配并修复**：扫网段 → 读每台设备的 IPP 身份（名称 / 型号 / 序列号 / UUID）→ 按身份给本机队列配对 → 把队列的端口指针改到设备当前 IP。设备换过 IP 之后队列「作业卡着不出纸」的问题，这一步就能解决。
- **重新探测**：只重扫设备、不动队列设置，保留当前选中和匹配结果。
- **刷新队列**：重新枚举本机队列（会重置选中状态）。
- **绑定所选 →**：自动配不上（或配错了）时，手动把左边某个队列绑到右边某台设备。
- **校准IP**：不扫全网、只看绑定表里记过的那几台设备。设备换过 IP 就按 UUID/序列号把它认回来，把端口指针改到新地址；认不出来就明说，不瞎改。首次点会先自动认一遍本机队列对应的设备（只读，不动任何设置）。命令行等价：`PrinterToolbox.exe --calibrate`
- **设为默认 / 打印测试页 / 删除队列 / 添加打印机 / 使用说明**
- **打印测试页**：走 Windows 队列（spooler → 端口 → 设备），验证整条链路。
- **IPP 直发**：绕开 Windows 队列，先问设备「你支持哪些文档格式」，再按格式把测试页直接交给设备（IPP `Print-Job` 到 631 端口）。见下面「Windows 说打印成功但不出纸」。

扫描范围默认留空 = 扫本机所有网段；也可以填 `192.168.1.0/24` 或 `192.168.10.1-60` 这种区间。

右栏多一列「**支持格式**」：设备自报的 `document-format-supported`。显示「未探测」说明 IPP 没问到，显示「设备未上报」说明设备拒答。**如果这一列里没有 `application/pdf`，那么用 Microsoft IPP 类驱动建的队列大概率打不出纸**（原因见下）。

### Windows 说打印成功但不出纸

这不是网络问题，是**格式问题**，2026-11 在一台 HP 一体机上定位过全过程：

1. Windows 的 `Microsoft IPP Class Driver` 配在标准 TCP/IP 端口（RAW 9100）上时，渲染产物是**原始 PDF**，裸推给端口（抓包实测 236,908 字节，文件头 `%PDF-1.7`）。
2. 有些喷墨一体机的 `document-format-supported` 里**没有 `application/pdf`**（只有 `image/pwg-raster`、`image/urf`、`image/jpeg`、`application/PCLm` 之类）。这类设备收到 PDF 会静默丢弃。
3. spooler 那边毫无异常：事件日志照样记 `id=307 … 打印页数: 1`，队列照样 `Normal`。于是「电脑提示成功、打印机不出纸」。
4. 换 HP 的通用驱动也一样：它发的是 PJL+PCL，可设备的语言白名单里同样没有 PCL6/PCLXL。

要判断一台设备是不是这种情况，用 `--ipp-test <IP>` 无头自检（无需桌面、无需管理员）：

```
PrinterToolbox.exe --ipp-test 192.168.1.100
```

**默认只探测、不打印。** 它会打印设备身份、`printer-state`、`printer-state-reasons`、支持的格式列表，以及「这台设备能不能被 Windows 队列正常打印」的结论，然后退出——不会出纸。

确认要真的打一张验证「纸上确实有字」时，才加 `--send`（会消耗 1 张纸）：

```
PrinterToolbox.exe --ipp-test 192.168.1.100 --send
```

界面上的「IPP 直发」按钮同理：点击后先问「真打 / 只看预览」，选「否」时只生成本地预览图并用系统看图工具打开，不耗纸。测试页本身也刻意做得很小（只有顶部几行短字，其余留白），一行小字就足以证明通道通。

**判据**：Windows 事件 `id=307` 只代表 spooler 把数据写进了端口，不代表出纸。真正可信的判据是设备自己的页数计数器 —— 走 9100 发一句 `@PJL INFO PAGECOUNT`：

```
PrinterToolbox.exe --ipp-test 192.168.1.100 --send   # 发之前取 @PJL INFO PAGECOUNT
# … 等 20~30 秒，再取一次 @PJL INFO PAGECOUNT
PrinterToolbox.exe --ipp-test 192.168.1.100 --send   # 再发一次，看是否递增
```

实测这台设备三次直发把页数从 9280 推到 9282，而同期 `id=307` 记了「已打印 1 页」的 Windows 队列一页都没出。

> 会话里踩过的坑：`Add-PrinterPort -Name 'http://ip/ipp/print' -PrinterHostAddress ip` **不会**建出真正的 IPP 端口，它仍然是一个 `MSFT_TcpIpPrinterPort`（`TCPMON.DLL`、9100、RAW），URL 只是端口名。只有 `Add-Printer -PortName <url>` 或 `printui /if` 那类路径才会生成 `Internet Port`，而实测这些通道对本例设备同样 delta=0。所以「换个端口形态」不能替代「换对文档格式」。

### ② 添加打印机（对话框，从 ① 页的按钮进）

两种加法并存，先扫后填都行：

- **扫描网络**：范围默认填好本机网段，点一下扫 631 / 9100 / 515，扫到的设备逐条列出来（地址 / 设备名 / 型号 / 服务）。**点中一行，地址、端口名、队列名、驱动全部自动填好**，点「添加」即可。扫描在后台跑，随时「停止」。
- **手填地址**：IP 或主机名都认；点「检测」先把型号/状态读出来再添加。填 `\\主机\共享名` 则按共享方式连接，不建端口。
- **USB 页**：枚举 USB 打印设备和 `USB00x` 端口，选设备即带出端口与型号。

扫描只是发 TCP 探测包，**不发打印数据、不改任何设置**；网段限制在 1024 个地址以内，超了会提示你缩范围。

### ③ 共享修复

针对「别的机器连我这台共享打印机报错」的成套开关，**逐项勾选、逐项生效**，左栏是项目、右栏是说明和副作用。

- **① 体检当前状态**：只读，不改任何东西，把每个开关的当前值和是否已被改过列出来。
- **② 应用勾选项**：改注册表 / 防火墙 / 可选功能。改之前自动把原值备份到数据目录，**还原上一次**一键回滚。
- **备份目录**：打开备份所在文件夹。

体检结果是三态，不是「不存在＝有问题」：

| 标记 | 含义 |
| --- | --- |
| `[已达标]` | 注册表里写了值，且正是期望的值 |
| `[默认即达标]` | 注册表里没这个值，但 Windows 不配置时的默认行为就等于期望行为，**不用改** |
| `[未达标]` | 还没达到，勾上应用即可 |

注册表项后面会附一行 `键：HKLM\...` 和枚举含义（例如 `0=警告并提权（未配置即此） 1=不警告不提权`），
这些默认值取自本机 `C:\Windows\PolicyDefinitions\*.admx`，不是猜的。注意同名值可能出现两次
（`AllowInsecureGuestAuth` 在服务键和策略键各一处），看键路径区分。

防火墙、LPR/LPD、打印后台这三项也会真体检，不是只给一句「需手动执行」：防火墙读
`FirewallPolicy\FirewallRules`（受限令牌下 `netsh` 会退出码 1，而这块注册表能读），
队列作业读 `Win32_PrintJob`（`spool\PRINTERS` 非管理员打不开）。

标记 `[降安全]` 的那几项会把机器往宽松方向推（例如允许不安全来宾认证、把驱动安装限制放宽），能不用就不用，用完了建议还原。

### ④ 驱动检测

- **① 扫描设备**：扫网段，读每台设备的 IPP 身份（厂商 / 型号 / 序列号）。
- **② 读驱动库**：读本机已装驱动（`Get-PrinterDriver`，带厂商、版本、INF 真实路径），加 `C:\ProgramData\PrinterToolbox\drivers` 下所有 `.inf`。
- **③ 自动匹配**：给每台设备算最合适的本机驱动，给出匹配依据和分数；配不上时给「通用驱动兜底」建议。
- **装建议驱动**：用 `pnputil` 装选中的 INF。**需要管理员**，非管理员会明确提示而不是静默失败。
- **打开驱动入口**：按设备型号生成驱动站搜索链接，浏览器打开自己挑。
- **驱动修复（联网）**：打开一个专门对话框，三件事一起做——
  1. **搜官方包**：按型号去驱动站查详情页，跟随 302 拿到厂商官方直链（HP 的会落到 `ftp.hp.com`），可「仅下载」也可「下载并安装」；也能手动改关键词搜，或直接开厂商官网。
  2. **判定兼容**：先问设备自己支持哪些页面语言（`@PJL INFO CONFIG`），再建一个临时队列、真打一份、抓驱动实际吐出的字节头（`%PDF` / `ENTER LANGUAGE=PCLXL` …），两者求交后给三态结论。**不打印就不判定**，因为这只能实测。
  3. **自动换驱动**：判定不兼容时，遍历本机已装驱动逐个试，找到能出设备认的语言的就用 `Set-Printer -DriverName` 原地换过去（免管理员），换完回读确认。

命令行等价：`PrinterToolbox.exe --drvcheck <IP>`（只判定、绝不打印）、`--drvcheck <IP> --capture`（抓包，会打测试页）、`--drvsearch <型号关键词>`（只搜不下载）。

驱动来源三个：**本机已装的** + **你放进驱动文件夹的** + **驱动修复对话框联网搜到的官方包**。默认不自动下载，每条都要你点。

### ⑤ 说明

四条使用要点 + 打开程序目录 / 数据目录。

---

## 从源码编译

不需要 Visual Studio、不需要 .NET SDK。Windows 10/11 自带 .NET Framework 4.x 运行时，里面就有 `csc.exe`：

```powershell
git clone https://github.com/2384418321/printer-toolbox.git
cd printer-toolbox
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-toolbox.ps1
```

产物在 `dist\PrinterToolbox.exe`。

编译脚本会自动：
- 找 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（找不到就退到 32 位版）
- 用 `/langversion:5`（代码按 C# 5 写，不用字符串插值 / `?.` / 元组，以兼容老编译器）
- 回读校验清单、产品名、签名状态

`-Dump` 参数会跑一次**无头布局自检**（不需要桌面，屏幕外建窗口导出所有控件坐标，报告越界与重叠）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-toolbox.ps1 -Dump
```

签名是可选的——没有代码签名证书就跳过，不影响产物可用性。想签就设环境变量：

```powershell
$env:PT_SIGN_SUBJECT = 'CN=你的证书主题'
```

---

## 权限模型

exe 的清单是 `asInvoker`——**双击起来不带管理员**，只读功能全部可用，这样不会一启动就弹 UAC。

需要写系统配置的动作（建端口 / 加队列 / 改共享注册表 / 装驱动）在点击时自己判断：

1. 先按普通权限试三条路：`Add-PrinterPort` → `prnport.vbs` → WMI；
2. 三条都不行，就用 `runas` 把自己重新拉起来，带命令行参数走无界面路径，干完把结果写回；
3. 还是不行，把三条路的真实报错原样打到日志里，并给一条可直接复制的管理员命令。

窗口右下角常驻一行：`管理员：有/没有　完整性：中/低（Low，受限）`。

**「完整性：低」是什么意思**：进程跑在低完整性级别下（沙箱、某些远程会话、被策略限制的启动方式），这时连 `C:\ProgramData` 都写不进去，任何写打印配置的动作都会失败——这不是工具的 bug。遇到这个标记，请正常在桌面上双击 exe，或用管理员权限运行。

---

## 匹配与打分是怎么做的

### 队列 ↔ 设备（页签 ①）

1. **读队列**：`Win32_Printer` 拿队列名、端口名、驱动名、状态。
2. **读端口**：`Win32_TCPIPPrinterPort` 拿端口指向的真实地址。
3. **读设备身份**：对每个候选地址发 IPP `Get-Printer-Attributes`，拿 `printer-name` / `printer-make-and-model` / `printer-info` / UUID / 序列号。
4. **打分**：
   - 端口地址 == 设备地址 → **+100**（最强证据，直接短路）
   - 序列号命中 → **+60**
   - 端口原始地址与 IPP 名称有包含关系 → **+45**
   - 型号词 ∩ 队列名 → **+35 起，每多一个词 +10，封顶 55**
   - 设备名 ∩ 队列名 → **+25 起，每多一个词 +8，封顶 40**
   - 驱动名 ∩ 型号 → **每词 +10，封顶 20**
5. **阈值**：最高分 **≥ 45** 且（只有一个候选，或第一名领先第二名 **≥ 15 分**）才采纳。宁可判「匹配不上」让你手动绑，也不硬塞一个错的。

匹配结果永远带「依据」一栏，能看见它凭什么这么配。

### 型号 ↔ 驱动（页签 ④）

1. **分词归一化**：全部转大写，剔除停用词（`series`、`printer`、`driver`、`class`、`universal`、`打印机`、`驱动`、`通用`、`支持`、`windows` 等），再按非字母数字切词，丢掉单字符碎片。这样 `HP OfficeJet Pro 9730 Series` 和 `HP OfficeJet Pro 9730` 会归到同一组词。
2. **打分**：型号词逐对比——两边都含数字且完全相等 **+45**（型号主干最硬）；纯字母相等 **+20**；一方包含另一方 **+10**；型号词出现在驱动名里 **+12**；驱动还带着真实 INF 路径再 **+3**（说明是真装过的，不是残留登记）。
3. **阈值**：最高分 **≥ 40** 才算匹配，否则直接判「本机没有这个驱动」。
4. **兜底**：配不上时按厂商挑通用驱动（名字里带 `UNIVERSAL` / `通用` 的那几条），给的是建议，不是自动安装。

---

## 两个踩过的坑（写在这里省你几小时）

### 1. Windows IPP 的字段全是大端序

IPP 的 `version-number`、`operation-id`、`request-id`、属性长度全是**大端（big-endian, 网络字节序）**，而 .NET 的 `BitConverter` 默认小端。直接 `BitConverter.ToUInt16` 会把长度字段 `00 12` 读成 `4608`，然后指针越界、把后面整个报文吞掉，症状是「IPP 请求发出去，设备回了东西，但解析出来全是空的」。

本项目的做法是**不手写完整二进制解析器**：把响应字节转成可见字符，用 `IndexOf` 找属性名，再往后 `Substring` 取可读值。对「只要读身份信息」这个用途足够了，而且不用维护一个 IPP 解析器。

### 2. C# 读 WMI 的 `HostAddress` 可能是空串

同一个 `Win32_TCPIPPrinterPort`，用 PowerShell 的 `Get-WmiObject` 读 `HostAddress` 有值，用 C# 的 `ManagementObjectSearcher` 读却可能返回**空字符串**（本项目开发机实测遇到，且不稳定复现——重开几次有时能读到）。

后果很隐蔽：端口字典命中、类型判定为 TCP/IP、但地址是空 → 状态列写「地址不通」→ 跳过所有探测 → 匹配时拿不到「同地址 +100 分」→ 只剩型号分和设备名分 → 低于阈值 → 界面显示「匹配不上」。

兜底办法（`PrinterSystem.cs` 的 `GetTcpPorts()` 里已实现）：`HostAddress` 为空且端口名是 `IP_<地址>` 形式时，直接从端口名里把地址抠出来。副作用为零——主机名端口不以 `IP_` 开头，WSD/USB/PORTPROMPT 根本不进这个分支。

> **核对方法**：如果你怀疑自己的工具也踩了这个坑，先 dump 端口字典的键和值，别只看 PowerShell 的输出——两者不等价。

---

## 诊断工具

`tools\` 下有两个一次性诊断程序，用来在没有 GUI 的情况下验证整条匹配管线：

```powershell
# 编译（需要先把 src\*.cs 一起编进去，详见 tools\README.md）
csc /target:exe /langversion:5 /out:bin\MatchProbe.exe ^
    /r:System.dll /r:System.Core.dll /r:System.Management.dll ^
    src\Model.cs src\Matcher.cs src\PrinterSystem.cs src\Scanner.cs src\Ipp.cs ^
    tools\ProbeMain.cs tools\MatchProbe.cs

# 运行：把你要验证的地址作为参数
.\bin\MatchProbe.exe 192.168.1.100 192.168.1.101
```

输出包括：队列表（含端口类型、解析出的地址、是否可修）、每台设备的探测耗时与存活判定、网段扫描结果、每台队列的匹配打分与是否采纳、以及最终的界面状态列文案。

---

## 文件构成

```
printer-toolbox\
  build-toolbox.ps1          编译 + 可选签名 + 回读校验
  src\
    Program.cs               入口（--dump-ui / --ipp-test / --calibrate / --drvcheck / --drvsearch / --apply-shared 等）
    ToolboxForm.cs           主窗口：四个页签
    MainForm.cs              页签①：校准 / 队列
    AddPrinterForm.cs        「添加打印机」对话框（网络 / USB）
    DriverRepairForm.cs      「驱动修复」对话框（联网搜包 / 兼容判定 / 自动换驱动）
    SharedFixPanel.cs        页签②：共享修复（含注册表备份与还原）
    DriverPanel.cs           页签③：驱动检测
    DriverStore.cs           驱动库枚举 / 匹配 / pnputil 安装
    DriverLookup.cs          驱动站搜索链接与型号推断
    DriverCompat.cs          设备语言探测 + 驱动输出抓包 + 兼容判定 + 原地换驱动
    PrinterSystem.cs         打印子系统读写（多路线回退 + 回读校验）
    Scanner.cs               网段扫描、TCP/ping 探测、IPP 身份解析
    Ipp.cs                   手写 IPP 协议（大端序处理，含 Print-Job）
    IppPrint.cs              生成测试页 JPEG / 判断 Windows 队列能不能打
    IppSelfTest.cs           无头 IPP 自检（--ipp-test）
    IppFilePrint.cs          文件直发（图片直发 / PDF 渲染后直发）
    IpRebind.cs              队列与设备的身份绑定表、换 IP 后自动校准
    IpRebindSelfTest.cs      无头校准（--calibrate）
    DriverSelfTest.cs        无头驱动自检与联网搜索（--drvcheck / --drvsearch）
    DriverOnline.cs          驱动站查找 / 下载 / 解包 / 安装
    Matcher.cs               队列-设备打分匹配
    Model.cs                 数据模型
    SelfTest.cs              自检
    UiDump.cs                布局自检（屏幕外建窗口导坐标）
    app.manifest             清单（asInvoker）
    icon.ico                 图标
  tools\                     无头诊断程序
    MatchProbe.cs
    ProbeMain.cs
  dist\                      编译产物（.gitignore 忽略）
```

运行时数据目录优先 `%ProgramData%\PrinterToolbox\`（备份 / 驱动库 / 日志）；写不进去时按
`%LOCALAPPDATA%\PrinterToolbox` → `%TEMP%\PrinterToolbox` → exe 同目录 `PrinterToolbox-Data`
逐级回退。低完整性进程（沙箱、某些远程会话）连 `%APPDATA%` 都写不了，不回退就什么都存不下来，
表现是「换 IP 后校准记不住设备」——绑定表 `bindings.tsv` 会落在第一个真正可写的那一级。

---

## 已知限制

- **只支持 Windows**。核心依赖 `Win32_Printer` / `Win32_TCPIPPrinterPort` / spooler API。
- **只认 IPv4**。IPv6 打印设备未实现。
- **IPP over 631 only**。用 9100 裸端口或 LPD 515 行协议的设备只能读到 TCP 可达性，读不到型号/序列号，因此只能靠地址匹配。
- **写动作需要管理员**。文档里「已实测 / 未实测」章节有详细说明。
- **驱动兼容判定必须真打一张**。`--drvcheck` 的「判定兼容」要抓驱动实际输出，只能建临时队列打一页测试页再读字节；`--capture` 和界面上的「判定兼容」都会耗一张纸，所以默认不自动跑。不想耗纸就用 `--drvcheck <IP>`（只读设备语言表 + 判定矩阵，不打印）。
- **联网搜驱动依赖站点结构**。走的是驱动站的搜索页与详情页正则，站点改版就会解析不到（会明确报「页面打开了，但没解析出下载链接」）。找不到时对话框里有「厂商官网」按钮兜底。
- **PDF 文件直发暂未接入界面**。`--ipp-test` 和「IPP 直发」按钮现在只处理 JPEG/PNG/TIFF 以及内置测试页；把 PDF 逐页渲染成 JPEG 的那条路径依赖 WinRT（`Windows.Data.Pdf`），在 .NET Framework 4 进程内加载会报 `HRESULT 0x80131515`，所以先留空。要打 PDF，先在阅读器里「打印为图片」，或用系统里能打的队列。

---

## 贡献

Issue 和 PR 都欢迎。提 Issue 时请附上：

- Windows 版本（`winver`）
- 打印机型号、连接方式（网络 / USB / 共享）、端口类型
- 如果是匹配问题：`--dump-ui` 的输出或日志文件（`%ProgramData%\PrinterToolbox\` 下）

代码约定：
- C# 5 语法（`/langversion:5`）——不要用字符串插值、`?.`、元组、`nameof`
- `.ps1` 脚本必须**纯 ASCII**（PS 5.1 会把无 BOM 的文件按 GBK 解码）
- 改完跑一次 `build-toolbox.ps1 -Dump` 确认布局没退步

---

## 许可证

[MIT](LICENSE)
