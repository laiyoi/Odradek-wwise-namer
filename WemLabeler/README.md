# WEM Labeler

WPF 桌面工具，用于对 [unused_wem_with_banks.csv](../unused_wem_with_banks.csv) 中未命名的 WEM 音频文件进行**文本标注**。支持通过 vgmstream 实时解码预览音频，波形可视化，以及批量导出带标签的 WAV 文件。

界面分为两个并列的标签页（像浏览器 / Gradio 那样点击上方切换）：

- **标注音频**：原来的主界面——WEM 列表、来源关联、波形、标注与导出
- **提取音频**：由原 Python 脚本迁移而来的音频流水线——路径设置 + 各处理步骤 + 内联日志输出

⓪ 号步骤**直接读取游戏文件**（不落任何资源 JSON），随后 ②③④ 把它加工成音频，最后回到「标注音频」页标注。

## 功能概览

- **CSV 加载**：解析 `unused_wem_with_banks.csv`，自动识别列名，支持含引号的 CSV 字段
- **实时音频预览**：通过 vgmstream-cli.exe 将 WEM 解码为 WAV → NAudio WASAPI 播放，纯软件流水线，不依赖系统解码器
- **来源关联**：显示该 WEM 的 WemRes 坐标（来自 `wem_index.json`）、**所属 bank**（CSV 的 `FoundInBankRes` / `BankCount` / `Banks` 列）
  与**所属 txtp 文件名**（CSV 的 `TxtpFiles` 列，多个用 `;` 分隔），以及文件大小
- **播放该 WEM 所属的 txtp**：选中条目后点「播放所属txtp」或按 `Ctrl+T`，自动反查引用该
  WemID 的 txtp 并作为音频播放，**无需手动拖入**；多个 txtp 引用同一 WEM 时弹出选择框。
  原有的拖入 txtp / 「文件 → 打开txtp预览」方式依然保留
- **提取音频标签页**（由原 Python 脚本迁移而来，**排在「标注音频」前面**）：
  ⓪ **从游戏文件直接读资源**（链路 + `wem_index.json` + `.bnk`）、**导出 WEM 原始音频**、
  路径设置（7 行，见下）、**一键下载 vgmstream + wwiser**、BNK 提取、
  **用 wwiser 生成 TXTP**、音频映射表构建、按映射表导出音频、按 Event ID 导出、
  未使用 WEM 分析、txtp 索引重建；所有步骤都在该页内联运行，带实时日志、进度条和取消按钮
- **工具位置固定**：所有外部工具都从 **exe 旁边的 `utils` 目录**里找（`<exe目录>\utils`），
  不会去别的地方找，都由「下载工具」自动装好：
  - `utils\vgmstream-cli.exe`（或 `utils\<解压出来的子目录>\vgmstream-cli.exe`，会递归找）
  - `utils\wwiser.pyz`、`utils\wwnames.db3`（**必须同目录**，见 ② 那节）
  - `utils\python\python.exe`（python.org 的嵌入式 Python，跑 wwiser 用；**不需要你装 Python**）
  - `utils\wwiser_cli.py`（随程序分发；见 ② 那节，用来绕开 wwiser 的 tkinter 依赖）

  找到后 vgmstream 路径会自动写进配置。
- **波形可视化**：基于 Canvas 绘制峰值波形，播放时高亮已播放部分，可点击波形跳转（Seek）
- **多声道下混**：自动将 3~8 声道音频下混为立体声，兼容任意声道数的 WEM 文件
- **标注管理**：
  - 在文本框中输入标注内容，切换文件时自动保存
  - 标注自动写回所加载的 CSV，每次保存即时更新
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
- **DS2 游戏本体**（仅 ⓪ 读游戏资源 / 导出 WEM 音频需要；标注与播放不依赖它）
- [vgmstream-cli.exe](https://github.com/vgmstream/vgmstream/releases)（用于 WEM → WAV 解码）——
  程序会**自动下载**，一般不需要手动准备

## 快速开始

### 1. 获取 vgmstream

**通常不用管**：首次启动时如果 exe 旁边的 `utils\` 里没有 `vgmstream-cli.exe`，
程序会后台自动从 [vgmstream-releases](https://github.com/vgmstream/vgmstream-releases) 的最新 release
下载 `vgmstream-win64.zip` 并解压到 `utils\vgmstream-win\`。想手工准备的话，把
`vgmstream-win64.zip` 解压到 `utils\` 下即可（会递归查找 `vgmstream-cli.exe`）。

### 2. 下载 / 构建 WemLabeler

**方式一：从 GitHub Actions 下载**

在仓库的 Actions 页面下载最新的 `WemLabeler` artifact。

**方式二：自行构建**

```bash
git clone <repo-url>
cd Odradek-wwise-namer
dotnet restore WemLabeler/WemLabeler.csproj
dotnet build WemLabeler/WemLabeler.csproj --configuration Release
```

`WemLabeler` 通过 `ProjectReference` 引用同仓库的 `OdradekSharp/`，所以上面这条命令会**一并构建它**，
不需要单独构建。

发布单文件：

```bash
dotnet publish WemLabeler/WemLabeler.csproj --configuration Release --runtime win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

发布产物里必须带上 `Data\types.json` 与 `Data\extensions.json`（由 csproj 的 `Content` 项自动拷过去，
见下「数据结构依赖」）。

### 3. 运行

首次启动时会自动下载 vgmstream（状态栏显示进度）。你也可以通过菜单 **文件 → 设置 vgmstream 路径** 随时更改。

然后通过 **文件 → 打开 CSV** 加载 [`unused_wem_with_banks.csv`](../unused_wem_with_banks.csv)，
或点「提取音频」页的 **⓪ 读游戏资源** 先把数据从游戏里读出来。

## 完整流程

```
⓪ 读游戏资源（直读游戏文件）    读 streaming_graph.core，一趟写出：
                               · sound_wem_mapping_export.json 的链路部分
                               · wem_index.json（WemID ↔ 对象坐标）
                               · Extracted_Banks/*.bnk（BankData 直接落盘）
        │
        ▼
② 用 wwiser 生成 TXTP           *.bnk → Extracted_Banks/txtp/*.txtp
   （导出 WEM 音频，约 10GB）    WwiseWemResource → WemResWem/*.wem
③ 富化音频映射表                 mapping 链路 + txtp / wem_index / banks.xml
                               → sound_wem_mapping_export.json（含音频源）
④ 按映射表导出音频               上面 → Exported_Audio/*.wav
分析未使用的 WEM                 基于 sound_wem_mapping_export.json → unused_wem_with_banks.csv
        │
        ▼
标注音频（另一个标签页）          打开 unused_wem_with_banks.csv 逐条听、打标注
```

## 界面布局

「标注音频」标签页：

```
┌────────────────────────────────────────────────────────────┐
│  文件  帮助                                                 │
├────────────────────────────────────────────────────────────┤
│ [ 提取音频 ] [ 标注音频 ]        ← 点击上方标签页切换         │
├────────────────────────────────────────────────────────────┤
│ [打开CSV...][重新加载][打开WEM文件夹...][打开音频目录]        │
│ [打开txtp预览...][导出标注CSV...][导出已标注WAV...][设置vgmstream路径...] │
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

顶部那一排按钮是最常用操作的快捷方式，多数与「文件」菜单里的项等价；
其中「打开音频目录」是本页独有的，菜单里则另有「导出解析后Txtp」。

「提取音频」标签页（Gradio 风格：上方输入、中间操作、下方输出）：

```
┌────────────────────────────────────────────────────────────┐
│  路径设置                                                   │
│   项目根目录:      [____________] [浏览...] [打开]           │
│   音频导出目录:    [____________] [浏览...] [打开]           │
│   Streaming WEM:   [____________] [浏览...] [打开]           │
│   txtp 目录:       [____________] [浏览...] [打开]           │
│   vgmstream:       [____________] [浏览...] [重新探测]       │
│   游戏根目录:      [____________] [浏览...] [自动查找]       │
│   WEM 音频目录:    [____________] [浏览...] [打开]           │
├────────────────────────────────────────────────────────────┤
│  音频流水线                                                 │
│   [⓪ 读游戏资源（直读游戏文件）] [导出 WEM 音频（约 10GB）]  │
│   [下载工具 (vgmstream + wwiser)] [工具目录]                │
│   [② 用 wwiser 生成 TXTP] [③ 富化音频映射表]               │
│   [③ 构建音频映射表] [④ 按映射表导出音频]                   │
│   [分析未使用的 WEM] [重建 txtp 索引]                        │
│   Event ID: [________] [按 Event ID 导出] [ ] 强制重建缓存   │
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
5. 勾选 **选中时自动播放** 复选框，切换文件时自动开始播放

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

通过顶部工具条的 **导出已标注WAV...**（等价于菜单 **文件 → 导出已标注 WAV**）选择目标文件夹。程序会自动：

1. 遍历所有已标注的文件
2. 使用 vgmstream 将每个 WEM 解码为 WAV
3. 以**标注内容**作为文件名保存

如果某个标注在同目录下产生文件名冲突，会自动追加 WemID 作为后缀。

## 提取音频标签页

原 `extract_bnk_from_json.py` / `export_sounds.py` / `export_by_id.py` / `link_unused_wem.py`
的功能已全部迁移进 **「提取音频」标签页**，产物格式与原脚本完全一致（已逐字节 / 逐行校验）：

| 按钮 | 等价操作 | 产物 |
| ---- | -------- | ---- |
| **⓪ 读游戏资源（直读游戏文件）** | odradek 的导出 + `extract_bnk_from_json.py`（**改为一步直读游戏文件**） | `sound_wem_mapping_export.json`（链路部分）、`wem_index.json`、`soundmap_report.txt`、`Extracted_Banks/*.bnk`；**不再落任何资源 JSON** |
| **导出 WEM 音频（约 10GB）** | —（新增） | 目标目录下的 `*.wem`（原始音频） |
| 下载工具 (vgmstream + wwiser + Python) | —（新增） | exe 旁边的 `utils\`：vgmstream（最新 release 的 win64 zip，解压）、`wwiser.pyz` + `wwnames.db3`、`python\`（python.org 的嵌入式 Python，约 13 MB）、`wwiser_cli.py`（随程序分发） |
| ② 用 wwiser 生成 TXTP | 手工开 wwiser 点「Generate TXTP」 | `Extracted_Banks\txtp\*.txtp`（bank 路径必须是相对 `../`） |
| ③ 富化音频映射表 | `export_sounds.py 1` 的 txtp/WEM 部分 | 覆写 `sound_wem_mapping_export.json`、`missing_wem_files.csv`、`mapping_build.log` |
| ④ 按映射表导出音频 | `export_sounds.py 2` | 输出目录下的 `*.wav`、`streaming_wem_map.csv`、`export_progress.json` |
| 按 Event ID 导出 | `export_by_id.py` | `Decoded_Audio_Split/{event}_{wem}.wav`、`wem_map_cache.json` |
| 分析未使用的 WEM | `link_unused_wem.py`（改为基于 `sound_wem_mapping_export.json` + `wem_index.json`） | `unused_wem_with_banks.csv`（列见下） |
| 重建 txtp 索引 | —（新增） | 内存索引，「播放所属txtp」在没有 `TxtpFiles` 列时使用 |

每个作业都**在本标签页内联执行**：后台线程运行，日志实时刷到下方文本框，
进度条显示进度，随时可以点「取消」中断，日志可以一键复制。

### ⓪ 读游戏资源（一步，不落任何资源 JSON）

**不再需要 odradek.exe / `links-*.db`，也不再需要原来的六个资源 JSON 目录。**
程序直接读 DS2 安装目录下的 `LocalCacheWinGame\package\streaming_graph.core`，一趟做完三件事：

1. **跳链**（纯元数据找对象 + 按需读组）：`GraphSoundResource → GraphProgramResource →
   NodeConstantsResource → WwiseID`，把结果写进 `sound_wem_mapping_export.json` 的**链路部分**
   （`ResourceName` / `GraphSound` 坐标 / `WwiseID`）。详见下面的 ③；
2. **写 `wem_index.json`**：`WemID → WwiseWemResource 坐标`（7,150 条）。它顶替了原来 7,838 个
   `WemResJson/*.json` —— WemID↔`.wem` 文件的对应、来源关联、未使用 WEM 分析都靠它；
3. **直接导出 `.bnk`**：把 `WwiseBankResource.BankData` 落成
   `Extracted_Banks/WwiseBankResource_<组>_<下标>.bnk`。
   所以「① 从 BankRes 提取 BNK」这一步**已经取消**。

实现来自同仓库的 `OdradekSharp/`（对 odradek 源码的逐行移植，`net10.0`），`WemLabeler` 通过
`ProjectReference` 引用它的 DLL。

- **中间产物只剩两类**：`Extracted_Banks/`（`.bnk` + `txtp/`）与 `WemResWem/`（`.wem`）
- **实测**（本机 DS2，4 线程）：图 `79,323` 组 / `5,354,196` 个 object / `241` 个文件；
  `5,700` 个 GraphSound → `10,611` 条目、`74` 个 bank、`7,150` 条 WEM 索引，链路 **33 s**
  （整步含 bank 落盘约 40 s），峰值内存约 **3 GB**
- **游戏根目录**：认「目录下有 `DS2.exe`」。可以手输、点「浏览...」选，或点那行的
  **「自动查找」**——它会先读注册表里的 Steam 路径与 `libraryfolders.vdf`，再扫各盘的
  `steamapps\common\*`，最后兜底浅扫盘根。

产出对比（旧的 6 类 JSON 导出 → 现在）：GraphSound `5,700` / GraphProgram `25,663` /
NodeConstants `26,385` / WwiseID `7,000` / WwiseWem `7,838` / WwiseBank `74` 这些数量都还在，
只是不再落盘成 JSON，而是在同一次读取里直接被消费掉。

### 导出 WEM 音频（约 10GB）

从每个 `WwiseWemResource` 取原始字节写成 `.wem`：streaming 的走 `StreamingDataSource`
（按 `Locator` 定位到 package 文件里的 `offset/length`），内嵌的走 `WemData`。

- 点击后**先弹窗说明体积约 10 GB**，并询问目录：
  **「是」**= 存到默认目录（即「WEM 音频目录」那一行，默认是 Streaming WEM 目录）；
  **「否」**= 自己选一个目录；**「取消」**= 不导出。
- 实测：读出的字节与既有导出**逐字节相同**（`.wem` 头部是 `RIFF....WAVEfmt `，
  且 `WemSize == StreamingDataSource.Length == 文件大小`）。
- 同样是增量的：目录里已存在的 `.wem` 会跳过。
- 磁盘提示：整份约 10 GB，请确认目标盘有足够空间。

### ③ 构建音频映射表（直读游戏数据跳链）

**链路不读 JSON，这一步也不读游戏。** ⓪ 已经把链路写进了 `sound_wem_mapping_export.json`；
③ 只做 **txtp 富化**：查 txtp 事件索引、`banks.xml` 的银行媒体表和 `wem_index.json`，
把 `TXTP_Filename` / `AudioSources`（含每个音频源对应的 WwiseWemResource 坐标）补上，覆写同一个文件。

跳链的核心在 [`Pipeline/SoundChainResolver.cs`](Pipeline/SoundChainResolver.cs)（⓪ 调用），
单进程、图只加载一次。链路字段（在 `types.json` 上逐一核实过）：

```
GraphSoundResource.ResourceName          : String                     ← 音效名
GraphSoundResource.GraphProgram          : Ref_GraphProgramResource
GraphProgramResource.ExposedDataResource : Ref_NodeConstantsResource
NodeConstantsResource.Parameters         : ProgramParameterList
  └ DefaultSoftLinkedObjects             : Array_Ref_RTTIRefObject    ← 可能有多个，也可能一个都不是
WwiseID.Id                               : uint32
```

记录形状（旧版还有 `GraphProgram` / `ExposedDataResource` / `WwiseID_Coord` 三个中间坐标，
现在跳链一次完成，不再需要）：

```json
{
  "ResourceName": "sd_env_wildfire_tree_fall_03",
  "GraphSound": "10009:156",
  "WwiseID": 1631881922,
  "TXTP_Filename": "WwiseBankResource_1604_366-0458-event.txtp",
  "AudioSources": [
    { "WemID": 719682266, "SourceType": "Embedded",
      "BankFile": "WwiseBankResource_1604_366.bnk",
      "WemRes_Coord": "1604:1266", "RawLine": "../WwiseBankResource_1604_366.bnk #s177 #i  ##719682266.wem" }
  ]
}
```

- `GraphSound` 是 **GraphSoundResource 的对象坐标 `组:下标`**（可直接拿去 `odradeksharp read`）。
- `WemRes_Coord` 由 `wem_index.json` 查得，是 **WwiseWemResource 的对象坐标 `组:下标`**。
  旧版这个字段写的是 `WemID:<数字>`，根本不是坐标，现已修正；查不到（例如 bank 内嵌媒体不是
  WwiseWemResource 对象）时为 `null`。

性能上必须这么做的原因（都是实测出来的，不是推测）：

1. **对象在流里没有长度/偏移**，要取组内第 *k* 个对象就必须顺序解出 `0..k`。所以
   「同一个组按不同下标读两趟」= 两组份工作量，**每个组必须只读一次**。
2. 做法：先用**纯元数据**（`group.types()`）把每个组里属于这四种类型的下标全部收集起来；
   第一次读该组时把它们一次读齐并缓存，之后任何下标请求都是缓存命中。
   实测：4 趟 × ~520 组 = **2,122 次组读取 → 583 次**，链路耗时 137 s → **33 s**。
3. **按需读组**：`ReadGroupFiltered(groupId, wanted, readSubgroups:false)`，只要链路上的对象，
   其余只留**类型桩**（不物化载荷），所以不会把组里无关的大数组拖进内存。
   不做子组递归（locator 照样解析）。
4. **有界 LRU 组缓存**（按 groupId，按整组对象槽位计费）。刻意**不**照抄「每组处理完就
   `ReleaseCaches()`」——那是为一次性批量导出设计的，走引用链会把大组反复重解析。
5. **先判类型再决定读不读**：soft-linked 目标用 `group.Types[i]` O(1) 判类型，不是 `WwiseID` 就不读。
   5,700 个音效共 17,014 个 soft-link，其中 137 个不是 WwiseID，一个字节都没读。
6. **并行**：默认 `min(4, CPU)` 个线程，每个线程一个 reader（`OdradekSharp` 已有 ThreadLocal reader）。

实测（本机 DS2，4 线程）：

| 项 | 值 |
| --- | --- |
| GraphSoundResource | 5,700（全部读出） |
| 产出条目 | 10,611（= 每条一个 WwiseID） |
| 不同 WwiseID | 6,361 |
| 链路涉及的组 | 2,514；实际读取 **583**，失败 0，淘汰重读 0 |
| 截断点之后的对象 | 0 |
| ⓪ 链路耗时 / ③ 富化耗时 | **33 s** / **12 s** |
| 峰值内存 | **约 3 GB** |

产出报告 `<项目根>\soundmap_report.txt`：总数 / 产出条目数 / 每个「本该有但拿不到」的对象明细
（哪个阶段、哪个 `组:下标`、什么原因）。

**与 Python 基准逐条比对**（`sound_wem_mapping_export.json`，10,611 条）：
`(ResourceName, WwiseID)` **10,611/10,611 完全一致**；`TXTP_Filename` 有 220 条不同 ——
原因是同一个 event ID 会命中多个 `.txtp`，谁胜出取决于目录枚举顺序（Python 与 C# 都如此，
是既有行为）。除这 220 条外，层内容与基准一致；`WemRes_Coord` 是按上面说的**故意**改成了真坐标。
`Extracted_Banks/*.bnk` 与旧「从 BankRes 提取」的产物 **74/74 逐字节相同**。

命令行（无界面）也能跑，方便对比和脚本化：

```
WemLabeler.exe soundmap [resources|mapping|unused|all] --base <项目根> --game <游戏根> \
    --out sound_wem_mapping_new.json --report soundmap_report.txt --log run.log
```

`resources` = ⓪，`mapping` = ③，`unused` = 分析未使用的 WEM，`all`（默认）= 全都跑。
`--out` 用来避免覆盖基准文件 `sound_wem_mapping_export.json`。

### 分析未使用的 WEM

产出的 `unused_wem_with_banks.csv` 列如下：

```
WemID,Coord,JsonFile,WemFile,WemPath,FoundInBankRes,TxtpFiles
```

比旧格式**只去掉了 `IsStreaming`** —— 这一列在本作里 7,838 个 WEM 全是 `true`，没有任何信息量，
是唯一明确不要的列。其余列一律保留。

`FoundInBankRes` = 「这个 WEM 被某个 bank 引用」（来自 `WwiseBankResource.WemIDs` 的并集，
由 ⓪ 写进 `wem_index.json` 的 `BankWemIDs`）。**不是** `banks.xml` 的媒体表 ——
那是「内嵌在 bank 里的媒体」，是另一个集合，实测两者差 4,280 行。

`WemPath` 是 WEM 文件的完整路径，需要 **Streaming WEM 目录** 指对；如果该目录不存在，
程序会**直接报错**，而不是默默写出一堆空路径。

**旧文件里的其他信息不会被清空**，这是刻意设计：

- 除上面这几列以外（以及被明确丢掉的 `IsStreaming`）的列都会按 WemID **原样保留** ——
  程序自己追加的 `Label` / `Duration` / `Channel`，或者你手加的任意列，重新生成时都不会被清掉；
- 旧文件里**已经不再是「未使用」的行**，会连同它的所有信息**整行追加到文件末尾**，
  日志里会提示这次带了多少行过来。

> 换句话说：新算出来的行在前面（按 WemID 排序），被「救回来」的历史行在末尾。
> 这样反复重新生成也不会丢掉任何人工填过的内容。
>
> `missing_wem_files.csv` 走同一套规则（基础列是 `WemID,Filename,Path`，同样只丢 `IsStreaming`）。
> 读 CSV 的那一侧也一样：`WriteCsvFile` 按原表头逐列写回，只过滤 `IsStreaming`，
> 表里没见过的列由 `ExtraColumns` 原样带回。

### ② 用 wwiser 生成 TXTP

实际执行的就是命令行（**工作目录 = `Extracted_Banks`，`-go` 必须给相对路径**）：

```
cd /d "<Extracted_Banks>"
"<exe目录>\utils\python\python.exe" "<exe目录>\utils\wwiser_cli.py" "<exe目录>\utils\wwiser.pyz" ^
    -g -go "txtp" "*.bnk"
```

> **`-go` 的写法很关键。** wwiser 给内嵌音频行写 bank 路径时，会把「输出目录的绝对路径层数 + 1」
> 当成 `../` 的个数。给绝对路径 `-go "E:\...\Extracted_Banks\txtp"`，它写出的是
> `../../../../WwiseBankResource_x_y.bnk` —— 从 txtp 目录解析出去直接指到盘根，
> **vgmstream 一律失败**（表现就是导出时满屏 `E0:失败 E1:失败 …`，而 `S0:OK` 的流式条目正常）。
> 给相对路径 `-go txtp` 才会写出正确的 `../WwiseBankResource_x_y.bnk`。

即便手上已经有那些用绝对 `-go` 生成的旧 txtp，**也不用重新生成**：导出时会先按 txtp 目录
正常解析，解析不到再按文件名去 `Extracted_Banks` 里找（`.bnk` 都平铺在那里，文件名唯一）。
实测 40,769 条内嵌音频源全部能解析到文件。

> **为什么走 `utils\wwiser_cli.py` 而不是直接跑 `wwiser.pyz`。**
> `wwiser.pyz` 是 Python zipapp，本来必须由解释器运行；而它的 `__main__.py` 会**无条件**
> `import wwiser.wgui`，`wgui` 又需要 `tkinter` —— python.org 的嵌入式 Python **不含 tkinter**，
> 直接跑会 `ModuleNotFoundError: No module named 'tkinter'`（哪怕只是命令行用法）。
> zipapp 本质是个 zip，加进 `sys.path` 就能直接 `import wwiser.wcli`，于是完全不需要 GUI 那一支。

**Python 解释器**：优先用 `utils\python\python.exe`（由「下载工具」从 python.org 下
`python-3.x.y-embed-amd64.zip` 解压，约 13 MB），**其次**才回落到 PATH 上的
`python` / `py` / `python3`。所以正常流程下**不需要用户自己装 Python**。
找不到时会弹框，点「是」直接跳到下载。

> **`wwnames.db3` 一定要和 `wwiser.pyz` 同目录。** 它是 wwiser 的 hash→名字库
> （SQLite，481 条），比如 `782826392 → default`、`964811743 → Lv2`：**有它，txtp 文件名里是
> `[3984055919=Lv2]`；没它，就退化成 `[3984055919=964811743]`**。
> 两次运行的输出目录不能混用，否则同一批 txtp 会出现两套名字、文件数变成并集。

wwiser 的进度输出在 stderr，程序会把 stdout 和 stderr 都实时打到日志区。

`WemIndexJson`（`wem_index.json`）的形状：

```json
{
  "WemDir": "...\\WemResWem",
  "Wems": { "1159743766": { "Coord": "31196:218", "LengthSeconds": 10.881 } },
  "BankWemIDs": ["123456", "..."]
}
```

- `Wems`：7,150 条 `WemID → WwiseWemResource 坐标`，其中 1,509 条带 `LengthSeconds`
  （`mLengthInSeconds`，其余为 0）。注意 **504 个 WemID 在图上出现在不止一个对象上**，
  所以坐标是「其中一个代表」，和旧版按 WemResJson 文件枚举顺序挑出来的可能不同（实测 142 行）。
- `BankWemIDs`：5,641 条，= 所有 `WwiseBankResource.WemIDs` 的并集（与旧的 BankRes JSON 完全一致）。

## 路径与自动探测

### 项目根目录怎么来的

`项目根目录` 会**自动探测**，判定标准是「该目录下存在 `Extracted_Banks` / `WemResWem` /
`sound_wem_mapping_export.json` / `wem_index.json` / `unused_wem_with_banks.csv` 之一
（旧布局的 `GraphSoundRes` / `BankRes` / `WemResJson` 仍然认，老配置不会失效）」，顺序是：

1. `config.json` 里存过的 `BaseDir`（或你手动「浏览...」选过的）；
2. 当前加载的 CSV 所在目录，逐级向上（最多 8 层）；
3. exe 所在目录，逐级向上（最多 8 层）。

其余所有目录（`Extracted_Banks` 及它的 `txtp\`、`WemResWem`、各导出目录）都由项目根目录派生。

> **打包给别人的话要注意**：exe 放在项目树里时第 3 条自然就能命中；把 exe 单独拷到别处
> （比如下载目录）运行时，往上 8 层都不会有那些标志目录，**自动探测必然失败**。
> 这种情况下程序**不会**拿 exe 目录充数（否则会出现 `C:\下载\WemLabeler\Extracted_Banks`
> 这种不存在的路径），而是把该项显示成 `(未设置)` 并弹出一条黄色提示条：
> 点任意流水线按钮、或点「浏览...」，选一次项目根目录即可，
> 选择结果会写进 `config.json`，以后不用再选。
> 「标注音频」标签页只依赖 CSV，不受此项影响，可以直接用。

### 游戏根目录怎么来的

只有 ⓪ 读游戏资源和「导出 WEM 音频」需要它，判据是**该目录下有 `DS2.exe`**。
可以手输、浏览，或点「自动查找」自动扫（见上）。

### 路径设置（7 行）

| 行 | 说明 |
| -- | ---- |
| 项目根目录 | 含 `Extracted_Banks` / `WemResWem` 等，通常自动探测 |
| 音频导出目录 | ④ 的输出目录 |
| Streaming WEM 目录 | `.wem` 文件所在处 |
| txtp 目录 | 默认 `Extracted_Banks\txtp` |
| vgmstream | vgmstream-cli.exe 路径（会自动在 exe 旁边的 utils 里找并回填） |
| 游戏根目录 | 含 `DS2.exe` 的那一层 |
| WEM 音频目录 | 「导出 WEM 音频」的默认输出目录 |

**所有路径框都可以直接手输**，改完立即写回 `config.json`，不用非得点「浏览...」。
多数行第三列是「打开」（在资源管理器里打开该目录；已选中 WEM 时「打开音频目录」会直接定位到该文件），
vgmstream 那行的第三列是「重新探测」（清空手工设置、重新自动探测项目根目录），
游戏根目录那行的第三列是「自动查找」。

「浏览...」用的是 .NET 8+ 的 `OpenFolderDialog`，也就是和「打开CSV」同一套标准资源管理器式对话框
（带地址栏、导航窗格、搜索框），不是旧版的树形选择框。

## CSV 数据结构

程序**读取** CSV 时认识的列（大小写不敏感，顺序任意）：

| 列名 | 说明 |
|------|------|
| WemID | WEM 文件 ID（必需） |
| Coord | 坐标信息（如 `1604:4570`），用于「来源关联」的 `WemRes:` |
| JsonFile | 源 JSON 文件名，同上 |
| WemSize | 文件大小（字节），用于「来源关联」的 `大小:` |
| WemFile | WEM 文件名 |
| WemPath | WEM 文件完整路径 |
| FoundInBankRes | 是否出现在某个 bank 的媒体表里（`是`/`否`，读 `banks.xml`） |
| BankCount | 关联的 bank 数量 |
| Banks | bank 文件列表 |
| TxtpFiles | 引用该 WEM 的 txtp 文件名，多个用 `;` 分隔 |
| Label | 已有标注（可由程序写出，也可预先存在） |
| Duration / Channel | 程序写出的额外列，再次加载时会被读回 |
| IsStreaming | **旧格式遗留，只读不写**：全项目已不再产出该列，但含该列的旧文件仍能正常加载 |

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

## 已知限制

- **3 个回调（`PhysicsShapeResource` / `PhysicsRagdollResource`（Jolt）、
  `FacialRigSettingWithLODResource`（RigLogic））都已移植**，真实数据上不再有截断。
  万一某个组撞上未实现的回调，兜底是**降级为「不读子组」再试一次**，组里的目标对象仍然能导出，
  代价是这些子组里的指针会退化成未解析的 `<ref>`。日志里会以 `[~]` 标记发生了降级。
  「构建映射表」直读游戏数据那一趟则会把这类对象逐条写进 `soundmap_report.txt`。
- **派生类型默认不导出**：`WwiseWemLocalizedResource`（`WwiseWemResource` 的派生类，
  实测 268 个对象）默认**不包含**在导出里
  （`WemLabeler/Pipeline/OdradekExporter.cs` 里 `IncludeDerivedTypes = false`）。原因有两个：
  与原 odradek 导出的数量一致；而且那些本地化对象只分布在少数几个超大的组里，
  读它们要几 GB 内存。现有 pipeline 也只 glob `WwiseWemResource_*.json`。
  将来若要纳入，需要同时放宽这个常量并调整 pipeline 的文件名匹配。
- **首次全量导出较重**：本地一个文件都没有时，要顺序读取约 2,600 个组（这正是当初在
  odradek GUI 里手工导出所做的事）。已加「每组读完强制 GC」来压低内存峰值。
- **「自动查找」游戏目录目前在 UI 线程上跑**，扫盘时界面会卡几秒。

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
  "ExportByIdDir": "D:\\Odradek-wwise-namer\\Decoded_Audio_Split",
  "GameRoot": "K:\\SteamLibrary\\steamapps\\common\\DEATH STRANDING 2 - ON THE BEACH",
  "WemAudioDir": "G:\\ds2_unpack\\wems\\WemResWem"
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
| GameRoot | DS2 游戏根目录（含 `DS2.exe`），仅 ⓪ / WEM 音频用 |
| WemAudioDir | 「导出 WEM 音频」的默认输出目录 |
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

## 数据结构依赖

`⓪ 读游戏资源` 依赖 `OdradekSharp/Data/` 下的两个文件：

- `types.json`（8.24 MB）——RTTI 类型模式，20,106 个类型的字段名 / 偏移 / 顺序 / 是否序列化
- `extensions.json`（9.2 KB）——扩展类型与扩展基类

csproj 会把它们拷到 exe 旁边的 `Data\` 下，运行期**必需**（没有它，游戏文件里一个字节都解释不了）。

> 这两个文件是 **odradek 自己仓库里的静态资源**
> （`odradek-game-ds2/src/main/resources/types.json` 的逐字节拷贝），**不是任何构建步骤生成的**，
> 所以必须随源码入库。git 的对象存储本身是压缩的，8.24 MB 的文件在仓库里实际只占约 1 MB。

## 技术栈

- **.NET 10.0** (WPF)
- **NAudio 2.3.0** — 音频播放与处理
- **System.Text.Json** — 配置、国际化文件与流水线数据的解析
- **OdradekSharp** — 同仓库的 Decima/DS2 读取器（`net10.0`，对 odradek 的逐行移植；
  项目类型是 `Exe`，但 WemLabeler 只用它的 DLL）。负责直接读取 `streaming_graph.core`、
  按类型搜索、反序列化并输出 odradek 兼容 JSON
- **vgmstream-cli** — WEM / txtp 解码引擎（预览、导出、流水线共用）

## 参考数据规模

本仓库开发时的实际规模，可作为性能预期：

| 项 | 规模 |
| -- | ---- |
| `Extracted_Banks\*.bnk` | 74 个 / 约 967 MB（⓪ 直接写，单个最大 58 MB） |
| `Extracted_Banks\txtp` | 约 11,715 个 `.txtp` |
| `WemResWem\*.wem` | 7,838 个 / 约 10 GB（导出 WEM 音频的产物） |
| `wem_index.json` | 7,150 条 WemID → 坐标 / 约 220 KB |
| `sound_wem_mapping_export.json` | 10,611 条 / 约 15 MB |
| `unused_wem_with_banks.csv` | 5,951 行 |
| ⓪ 读游戏资源 | 5,700 个 GraphSound → 10,611 条目、74 个 bank、7,150 条 WEM 索引；链路 33 s / 约 40 s |
| ③ 富化映射表 | 11,715 个 txtp + 515 MB `banks.xml`；约 12 s |

> 旧布局那六个资源 JSON 目录（`WemResJson` 7,838 / `BankRes` 74 / `GraphSoundRes` 5,700 /
> `GraphPgmRes` 25,663 / `NodeConstRes` 26,385 / `WwiseID` 7,000）**已经不再产出**。
> 其中 `GraphSoundRes` 若你以前导出过，可以留着当核对用的基准；其余都可删。

## 许可

本项目仅供学习和研究使用。
