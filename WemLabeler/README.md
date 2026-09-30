# WEM Labeler

WPF 桌面工具，用于对 [unused_wem_with_banks.csv](../unused_wem_with_banks.csv) 中未命名的 WEM 音频文件进行**文本标注**。支持通过 vgmstream 实时解码预览音频，波形可视化，以及批量导出带标签的 WAV 文件。

界面分为两个并列的标签页（像浏览器 / Gradio 那样点击上方切换）：

- **标注音频**：原来的主界面——WEM 列表、来源关联、波形、标注与导出
- **提取音频**：由原 Python 脚本迁移而来的音频流水线——路径设置 + 各处理步骤 + 内联日志输出

## 功能概览

- **CSV 加载**：解析 `unused_wem_with_banks.csv`，自动识别列名，支持含引号的 CSV 字段
- **实时音频预览**：通过 vgmstream-cli.exe 将 WEM 解码为 WAV → NAudio WASAPI 播放，纯软件流水线，不依赖系统解码器
- **来源关联**：显示该 WEM 的 WemRes 坐标、**所属 bank**（CSV 的 `FoundInBankRes` / `BankCount` / `Banks` 列）
  与**所属 txtp 文件名**（CSV 的 `TxtpFiles` 列，多个用 `;` 分隔），以及文件大小
- **播放该 WEM 所属的 txtp**：选中条目后点「播放所属txtp」或按 `Ctrl+T`，自动反查引用该
  WemID 的 txtp 并作为音频播放，**无需手动拖入**；多个 txtp 引用同一 WEM 时弹出选择框。
  原有的拖入 txtp / 「文件 → 打开txtp预览」方式依然保留
- **提取音频标签页**（由原 Python 脚本迁移而来，**排在「标注音频」前面**）：
  路径设置（项目根目录 / 音频导出目录 / Streaming WEM 目录 / txtp 目录 / vgmstream）、
  **一键下载 vgmstream + wwiser**、BNK 提取、
  **用 wwiser 生成 TXTP**、音频映射表构建、按映射表导出音频、按 Event ID 导出、
  未使用 WEM 分析、txtp 索引重建；所有步骤都在该页内联运行，带实时日志、进度条和取消按钮
- **工具位置固定**：vgmstream 与 wwiser 只从 **exe 旁边的 `utils` 目录**里找
  （`<exe目录>\utils`），不会去别的地方找：
  - `utils\vgmstream-cli.exe`（或 `utils\<解压出来的子目录>\vgmstream-cli.exe`，会递归找）
  - `utils\wwiser.pyz`
  找到后 vgmstream 路径会自动写进配置。
- **波形可视化**：基于 Canvas 绘制峰值波形，播放时高亮已播放部分，可点击波形跳转（Seek）
- **多声道下混**：自动将 3~8 声道音频下混为立体声，兼容任意声道数的 WEM 文件
- **标注管理**：
  - 在文本框中输入标注内容，切换文件时自动保存
  - 标注自动写入 CSV（`labeled_wem_files.csv`），每次保存即时更新
  - 已标注文件在列表中绿色高亮显示 ✓
- **导出功能**：
  - **导出标注 CSV**：手动选择路径导出完整标注结果
  - **导出已标注 WAV**：批量解码所有已标注文件的 WEM → WAV，以标注内容作为文件名
- **国际化**：支持中文 / English 切换，配置持久化
- **键盘快捷键**：← → 切换文件 / Space 播放停止 / Ctrl+T 播放所属txtp / Ctrl+S 保存标注 / Enter 在标注框内保存
- **日志系统**：vgmstream 解码日志写入 `logs/` 目录，便于排查问题

## 系统要求

- Windows 10 x64 或更高版本
- [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)（或更高版本）
- [vgmstream-cli.exe](https://github.com/vgmstream/vgmstream/releases)（用于 WEM → WAV 解码）

## 快速开始

### 1. 获取 vgmstream

从 [vgmstream releases](https://github.com/vgmstream/vgmstream/releases) 下载最新版本，解压后获得 `vgmstream-cli.exe`。

### 2. 下载 / 构建 WemLabeler

**方式一：从 GitHub Actions 下载**

在仓库的 [Actions](https://github.com/ShadelessFox/odradek/actions) 页面下载最新的 `WemLabeler` artifact。

**方式二：自行构建**

```bash
git clone <repo-url>
cd Odradek-wwise-namer
dotnet restore WemLabeler/WemLabeler.csproj
dotnet build WemLabeler/WemLabeler.csproj --configuration Release
```

发布单文件：

```bash
dotnet publish WemLabeler/WemLabeler.csproj --configuration Release --runtime win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

### 3. 运行

首次启动时会提示设置 `vgmstream-cli.exe` 的路径。你也可以通过菜单 **文件 → 设置 vgmstream 路径** 随时更改。

然后通过 **文件 → 打开 CSV** 加载 [`unused_wem_with_banks.csv`](../unused_wem_with_banks.csv)。

## CSV 数据结构

程序期望的 CSV 包含以下列（大小写不敏感，顺序任意）：

| 列名 | 说明 |
|------|------|
| WemID | WEM 文件 ID |
| Coord | 坐标信息（如 `2:878`） |
| JsonFile | 源 JSON 文件名 |
| IsStreaming | 是否为 Streaming 音频 |
| WemSize | 文件大小（字节） |
| WemFile | WEM 文件名 |
| WemPath | WEM 文件完整路径 |
| FoundInBanks | 是否在 banks 中 |
| BankCount | 关联的 bank 数量 |
| Banks | bank 文件列表 |

如 CSV 中已包含 `Label` 列，程序会自动识别并加载已有的标注。

## 使用方法

```
┌────────────────────────────────────────────────────────────┐
│  文件  帮助                                                 │
├────────────────────────────────────────────────────────────┤
│ [ 标注音频 ] [ 提取音频 ]        ← 点击上方标签页切换         │
├──────────────┬─────┬───────────────────────────────────────┤
│ ✓  WemID     │     │  文件信息                             │
│    Filename  │     │  WemID / 路径                         │
│    Label     │ 分  │  来源关联                             │
│              │     │  WemRes / Banks / Txtp / 大小         │
│  文件列表    │ 隔  │                                       │
│  (左侧)     │     │  [播放] [停止] [播放所属txtp] [自动播放]│
│              │ 条  │  ┌──────────────────────┐             │
│              │     │  │   波形图 (Canvas)     │             │
│              │     │  └──────────────────────┘             │
│              │     │  标注内容: [__________]               │
│              │     │  [保存] [上一个] [下一个]             │
│              │     │  进度: 12 / 50                        │
├──────────────┴─────┴───────────────────────────────────────┤
│  状态栏                                                     │
└────────────────────────────────────────────────────────────┘
```

「提取音频」标签页的布局（Gradio 风格：上方输入、中间操作、下方输出）：

```
┌────────────────────────────────────────────────────────────┐
│  路径设置                                                   │
│   项目根目录:      [____________________] [浏览...] [打开]  │
│   音频导出目录:    [____________________] [浏览...] [打开]  │
│   Streaming WEM:   [____________________] [浏览...] [打开]  │
│   txtp 目录:       [____________________] [浏览...] [打开]  │
│   vgmstream:       [____________________] [浏览...] [重新探测]│
├────────────────────────────────────────────────────────────┤
│  音频流水线                                                 │
│   [① 提取BNK] [② 构建映射] [③ 导出音频] [②+③ 一键完成]     │
│   [分析未使用WEM] [重建txtp索引]                            │
│   Event ID: [________] [按 Event ID 导出] [ ] 强制重建索引   │
├────────────────────────────────────────────────────────────┤
│  输出                                                       │
│   [ 日志文本区 ]                                            │
│   [进度条]            [复制日志] [清空日志] [取消]           │
└────────────────────────────────────────────────────────────┘
```

### 基本操作

1. 双击列表项或点击 **播放** 按钮预览音频
2. 在右下方的文本框中输入标注内容
3. 点击 **保存标注**（或按 Ctrl+S）保存
4. 使用 ← → 键切换到上一个/下一个文件（自动保存当前标注）
5. 勾选 **自动播放** 复选框，切换文件时自动开始播放

### 快捷键

| 快捷键 | 操作 |
|--------|------|
| ← | 上一个文件（自动保存） |
| → | 下一个文件（自动保存） |
| Space | 播放 / 停止音频 |
| Ctrl+T | 播放当前 WEM 所属的 txtp |
| Ctrl+S | 保存当前标注 |
| Enter | 在标注框中按下即保存 |

### 来源关联

选中条目后，「来源关联」会显示：

| 行 | 来源 | 说明 |
|----|------|------|
| `WemRes:` | `Coord` / `JsonFile` 列 | 资源坐标，如 `1604:4570` |
| `Banks:` | `FoundInBankRes` / `BankCount` / `Banks` 列 | 所属 bank；只有是/否时显示 `✓ 有引用` 或 `—` |
| `Txtp:` | `TxtpFiles` 列 | 引用该 WEM 的 txtp 文件名，多个用 `;` 分隔 |
| `大小:` | `WemSize` 列 | 文件大小 |

这几项都直接来自 CSV，不会额外解析 banks.xml。

### 播放该 WEM 所属的 txtp

列表里的每个 WEM 都可能被一个或多个 txtp 引用（txtp 是 wwiser 生成的、把 bank 内嵌音频
按事件组织起来的描述文件）。选中条目后点击 **播放所属txtp**（或按 `Ctrl+T`）即可直接听到
该音频的完整事件混音，**不需要先手动把 txtp 拖进窗口**。

查找顺序：

1. CSV 里已有的 `TxtpFiles` 列（由「分析未使用的 WEM」生成）——**正常情况走这一条**；
2. 只有当 CSV 没有 `TxtpFiles` 列（或该行为空）时，才退回到运行期建立的 txtp 反向索引
   （`WemID → 引用它的 txtp`）：首次使用扫描一次 txtp 目录（约 1 秒 / 1 万个文件），
   之后走缓存；也可以手动点「提取音频」页的 **重建 txtp 索引**。

如果同一个 WEM 被多个 txtp 引用，会弹出选择框让你挑一个。

解析 txtp 里的音频行时，`../WwiseBankResource_x_y.bnk` 这类内嵌引用按 txtp 所在目录解析，
`wem/<id>.wem` 这类 Streaming 引用则先查已加载的 CSV，再查 `WemResWem` 目录里的实际文件。

原有的两种手动方式依然可用：直接把 `.txtp` 文件拖进窗口，或 **文件 → 打开txtp预览**。

### 导出 WAV

通过菜单 **文件 → 导出已标注 WAV**，选择目标文件夹。程序会自动：

1. 遍历所有已标注的文件
2. 使用 vgmstream 将每个 WEM 解码为 WAV
3. 以**标注内容**作为文件名保存

如果某个标注在同目录下产生文件名冲突，会自动追加 WemID 作为后缀。

## 提取音频标签页

原 `extract_bnk_from_json.py` / `export_sounds.py` / `export_by_id.py` / `link_unused_wem.py`
的功能已全部迁移进 **「提取音频」标签页**，产物格式与原脚本完全一致（已逐字节 / 逐行校验）：

| 按钮 | 等价操作 | 产物 |
| ---- | -------- | ---- |
| 下载工具 (vgmstream + wwiser) | —（新增） | exe 旁边的 `utils\`：vgmstream（最新 release 的 win64 zip，解压）与 `wwiser.pyz`（最新 release 的单文件 zipapp） |
| ① 从 BankRes 提取 BNK | `extract_bnk_from_json.py` | `Extracted_Banks/*.bnk` |
| ② 用 wwiser 生成 TXTP | 手工开 wwiser 点「Generate TXTP」 | `Extracted_Banks\txtp\*.txtp` |
| ③ 构建音频映射表 | `export_sounds.py 1` | `sound_wem_mapping_export.json`、`missing_wem_files.csv`、`mapping_build.log` |
| ④ 按映射表导出音频 | `export_sounds.py 2` | 输出目录下的 `*.wav`、`streaming_wem_map.csv`、`export_progress.json` |
| 按 Event ID 导出 | `export_by_id.py` | `Decoded_Audio_Split/{event}_{wem}.wav`、`wem_map_cache.json` |
| 分析未使用的 WEM | `link_unused_wem.py` | `unused_wem_with_banks.csv` |
| 重建 txtp 索引 | —（新增） | 内存索引，「播放所属txtp」在没有 `TxtpFiles` 列时使用 |

**② 用 wwiser 生成 TXTP** 实际执行的就是命令行：

```
python <exe目录>\utils\wwiser.pyz -g -go "<Extracted_Banks>\txtp" "<Extracted_Banks>\*.bnk"
```

`wwiser.pyz` 是 Python zipapp，需要 Python 3 —— 程序直接在 PATH 上找
`python` / `py` / `python3`，**不需要任何配置**；找不到时会提示装 Python。
wwiser 的进度输出在 stderr，程序会把 stdout 和 stderr 都实时打到日志区。

> 小提示：把 `wwnames.db3` 和 `wwiser.pyz` 放在同一个目录（即 exe 旁边的 `utils\`），
> wwiser 就能用上人工整理的名称，生成的 txtp 文件名会更可读。

每个作业都**在本标签页内联执行**：后台线程运行，日志实时刷到下方文本框，
进度条显示进度，随时可以点「取消」中断，日志可以一键复制。

**路径设置**（标签页顶部）用于指定：

- 项目根目录（含 `GraphSoundRes` / `WemResJson` / `BankRes` 等，通常会自动探测）
- 音频导出目录
- Streaming WEM 目录（`.wem` 文件所在处）
- txtp 目录（默认 `Extracted_Banks\txtp`）
- vgmstream-cli.exe 路径（会自动在 exe 旁边的 utils 里找并回填）

每项后面有「浏览...」和「打开」按钮，最后的「重新探测」会清空手工设置、重新自动探测项目根目录。

> 注意：「分析未使用的 WEM」会重新生成 `unused_wem_with_banks.csv`，覆盖其中由本工具追加的
> `Label` / `Duration` / `Channel` 三列。程序在检测到当前正加载该文件时会询问是否立即重新加载。

## 启动与故障排查

- **窗口会强制前置**：从终端（尤其是最大化的终端 / `dotnet run`）启动时，Windows 不一定会把
  新窗口提到前台，可能完全被终端挡住。程序在窗口渲染完成后会主动 `SetForegroundWindow`，
  保证窗口一定可见。
- **vgmstream 全自动，不再弹配置框**：启动时如果 exe 旁边的 `utils\` 里没有
  `vgmstream-cli.exe`，程序会**后台自动下载**到那里（状态栏显示下载进度），
  期间照常用其它功能；播放 / 导出等动作在需要时会等它下载完。
  只有在下载**失败**时才弹一个错误框说明原因，并提示可以手动放进 `utils\`。
  想手工指定别的 vgmstream，仍可用 **文件 → 设置 vgmstream 路径**。
- **崩溃可见**：`WemLabeler` 是 GUI 子系统程序（没有控制台），未处理异常默认会让进程
  静默消失。现在所有未处理异常都会弹框提示并写入 `logs/crash_YYYYMMDD.log`。
- **临时目录不可写时自动回退**：WPF 会在 `%TEMP%\WPF` 下创建临时文件。如果在受限环境
  （沙箱、被改写过权限的临时目录）里运行，这里会抛
  `UnauthorizedAccessException: Access to the path '...\Temp\WPF\...' is denied.`。
  程序启动时会先探测系统临时目录，不可写就自动把 `TEMP`/`TMP` 指向
  `<程序目录>\temp`（不行再退到 `%LOCALAPPDATA%\WemLabeler\temp`），
  并在 `logs/startup_YYYYMMDD.log` 记录这次重定向。
- **「下载工具」连不上 GitHub**：程序直连，走系统网络设置（路由器上的 fake-ip / 透明代理
  会被路由器自己转出去，不需要额外配置）。如果确实失败，日志里会给出完整错误链；
  也可以自己下载后放到 exe 旁边的 `utils\` 下，程序照样认：
  - vgmstream：下载 `vgmstream-win64.zip` 解压到 `utils\`
  - wwiser：下载 `wwiser.pyz` 放到 `utils\`
- **注意**：`utils` 在 exe 旁边（也就是 `WemLabeler\bin\<配置>\net10.0-windows\utils`），
  属于编译输出目录，`dotnet clean` 或删掉 `bin` 会一起没了；重新编译后需要再跑一次
  「下载工具」，或者把工具重新拷进去。
- **启动日志**：`logs/vgmstream_YYYYMMDD.log` 会记录
  `MainWindow ctor: begin/done`、`MainWindow loaded` 等标记，
  可以据此判断程序启动到了哪一步。

## 配置文件

首次运行后，程序会在所在目录生成 `config.json`：

```json
{
  "LastCsvPath": "D:\\Odradek-wwise-namer\\unused_wem_with_banks.csv",
  "VgmstreamPath": "E:\\vgmstream\\vgmstream-cli.exe",
  "AutoPlay": false,
  "Language": "zh-CN",
  "BaseDir": "D:\\Odradek-wwise-namer",
  "OutputAudioDir": "G:\\ds2_unpack\\wems\\Exported_Audio",
  "WemResWemDir": "G:\\ds2_unpack\\wems\\WemResWem",
  "TxtpDir": "D:\\Odradek-wwise-namer\\Extracted_Banks\\txtp",
  "ExportByIdDir": "D:\\Odradek-wwise-namer\\Decoded_Audio_Split"
}
```

| 字段 | 说明 |
|------|------|
| LastCsvPath | 上次打开的 CSV 路径（启动时可选择恢复） |
| VgmstreamPath | vgmstream-cli.exe 的路径 |
| BaseDir | 项目根目录，流水线所有输入目录都由它推导；留空则自动探测 |
| OutputAudioDir | 「按映射表导出音频」的输出目录 |
| WemResWemDir | Streaming `.wem` 文件所在目录 |
| TxtpDir | txtp 目录（留空则用 `Extracted_Banks\txtp`） |
| ExportByIdDir | 「按 Event ID 导出音频」的输出目录 |
| AutoPlay | 是否启用自动播放 |
| Language | 界面语言（`zh-CN` 或 `en-US`） |

## 音频播放流水线

```
vgmstream-cli.exe -o temp.wav <input.wem>
       │
       ▼
  临时 WAV 文件 (File.ReadAllBytes → File.Delete)
       │
       ▼
  NAudio WaveFileReader → ISampleProvider (float)
       │
       ▼
  [StereoDownmixProvider] (多声道→立体声，仅当 >2ch)
       │
       ▼
  FloatToPcm16 (float → 16-bit PCM byte[])
       │
       ▼
  RawSourceWaveStream → WasapiOut (WASAPI 播放)
```

- 解码后的 PCM 数据缓存在内存中，切换文件后无需重新解码即可恢复播放
- 波形峰值在解码时一并计算，无需额外开销

## 日志

vgmstream 的解码日志保存在程序目录的 `logs/vgmstream_YYYYMMDD.log` 中。当解码失败时，程序会弹窗提示，可选择直接打开日志目录查看详情。

未处理异常会写入 `logs/crash_YYYYMMDD.log` 并弹框显示，便于排查「双击没反应」这类问题。

## 技术栈

- **.NET 10.0** (WPF)
- **NAudio 2.3.0** — 音频播放与处理
- **System.Text.Json** — 配置、国际化文件与流水线数据的解析
- **vgmstream-cli** — WEM / txtp 解码引擎（预览、导出、流水线共用）

## 许可

本项目仅供学习和研究使用。
