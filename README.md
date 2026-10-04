# Odradek-wwise-namer

<p align="center">
  <a href="README_EN.md">English Version</a>
</p>

基于 [Odradek](https://github.com/ShadelessFox/odradek) 的音频文件命名与导出工具，专为 **Death Stranding 2** 设计。

## 简介

[Odradek](https://github.com/ShadelessFox/odradek) 是 Horizon Forbidden West 的资源查看器和提取器，是 [Decima Workshop](https://github.com/ShadelessFox/decima) 的重生版本，专为 Decima 引擎游戏的模组制作者设计。

本项目在它之上做两件事：

1. **导出资源** —— 直接读取 DS2 安装目录里的 `streaming_graph.core`，按类型搜索对象并写出 JSON。
   **不需要打开 Odradek 的图形界面，也不需要 `odradek.exe`。**
   这一步由仓库内的 [OdradekSharp/](OdradekSharp/) 完成（另一个 agent 对 Odradek 的 C# 移植，图统计与导出结果均已实测一致）。
2. **命名与导出音频** —— BNK 提取 → wwiser 生成 TXTP → 构建映射表 → 导出 WAV，外加对未使用 WEM 的文本标注。

整条流水线都在 **WemLabeler** 的图形界面里，两个标签页：**「提取音频」在前，「标注音频」在后**。

> `pyscript/` 下仍保留着 7 个 Python 脚本，作为最初的参考实现（读写完全相同的文件格式），但已不是主路径。

## 前置需求

| 需要 | 用途 |
| --- | --- |
| **Death Stranding 2** 游戏本体 | ⓪ 导出资源的来源，需要 `DS2.exe` 所在的那一层目录 |
| [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | **构建** WemLabeler |
| [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | **运行** WemLabeler（使用发布好的单文件版时） |
| Python 3.x | **仅 ②「用 wwiser 生成 TXTP」需要**；解释器会自动在 `PATH` 里查找，不用配置 |
| [wwiser](https://github.com/bnnm/wwiser) · [vgmstream](https://github.com/vgmstream/vgmstream) | 程序**自动下载**，见下 |

**不再需要** [Odradek](https://github.com/ShadelessFox/odradek) 本体或 `odradek.exe`。

### 工具会自动下载到哪里

两个外部工具都放在 **exe 旁边的 `utils\`**（不是项目根目录的 `utils\`）：

| 工具 | 来源 | 落点 |
| --- | --- | --- |
| vgmstream | `vgmstream/vgmstream-releases` 的最新 release | `<exe>\utils\vgmstream-win\` |
| wwiser | `wwiser.pyz` | `<exe>\utils\` |

把 `wwnames.db3` 放在 `wwiser.pyz` 旁边（即 `<exe>\utils\`），生成的 txtp 里就是可读的 event 名字。
也可以点 **「下载工具 (vgmstream + wwiser)」** 手动触发；**「工具目录」** 按钮会打开该目录。

## 使用步骤

全部在 WemLabeler 的两个标签页里完成：

- **提取音频**（默认打开）：整条流水线，按 ⓪ → ① → ② → ③ → ④ 的顺序走一遍
- **标注音频**：读 CSV、听音频、写标注

按钮与原始 Python 脚本的对应关系见 [原 Python 脚本（参考实现）](#原-python-脚本参考实现)。

### ⓪ 导出资源（直读游戏文件）

点 **「⓪ 导出资源（直读游戏文件）」**。

- 直接读 `<游戏根>\LocalCacheWinGame\package\streaming_graph.core`
- 游戏目录的判据是**该目录下有 `DS2.exe`**；点 **「自动查找」** 会扫各盘 Steam 库自动定位
- 按类型搜索对象是**纯元数据操作，不做反序列化**；命中的对象才逐个读取并写出 JSON
- **增量**：已存在的文件直接跳过，中断后重跑接着走

图与对象数量的实测值（DS2 2026-10 版本）：

| 项 | 值 |
| --- | --- |
| 图规模 | 79,323 个组 / 5,354,196 个 object / 241 个文件 |

| 类型 | 对象数 | 输出目录 |
| --- | --- | --- |
| WwiseWemResource | 7,838 | `<项目根>\WemResJson` |
| WwiseBankResource | 74 | `<项目根>\BankRes` |
| GraphSoundResource | 5,700 | `<项目根>\GraphSoundRes` |
| GraphProgramResource | 25,663 | `<项目根>\GraphPgmRes` |
| NodeConstantsResource | 26,385 | `<项目根>\NodeConstRes` |
| WwiseID | 7,000 | `<项目根>\WwiseID` |

这些数量与 Odradek 实际导出的文件数**一一相等**。

### ① 从 BankRes 提取 BNK

点 **「① 从 BankRes 提取 BNK」**。程序会：

- 读取 `BankRes` 中的 JSON
- 解码 Base64 编码的 `BankData`
- 修复 Wwise Bank 数据的对齐问题
- 把 `.bnk` 写入 `Extracted_Banks\`

### ② 用 wwiser 生成 TXTP

点 **「② 用 wwiser 生成 TXTP」**。实际执行的命令是：

```bash
python "<exe>\utils\wwiser.pyz" -g -go "<项目根>\Extracted_Banks\txtp" "<项目根>\Extracted_Banks\*.bnk"
```

生成的 `.txtp` 落在 `Extracted_Banks\txtp\`。

> 这一步需要 Python 3.x 在 `PATH` 里。这一步骤原先要手动打开 wwiser 点「Generate TXTP」。

### ③ 构建音频映射表

点 **「③ 构建音频映射表」**。解析所有 JSON 与 TXTP，生成 `sound_wem_mapping_export.json`：

- 关联 GraphSoundResource / GraphProgramResource / NodeConstantsResource / WwiseID
- 记录缺失的 WEM 到 `missing_wem_files.csv`
- 只读不写，速度很快

### ④ 按映射表导出音频

点 **「④ 按映射表导出音频」**。基于已构建的映射表导出 WAV：

- 跳过重复的 JSON 解析
- 由 vgmstream 解码，支持断点续传（进度记在 `export_progress.json`）
- 默认输出到 `<项目根>\Exported_Audio`，可在路径设置里改

### 导出 WEM 音频（约 10 GB）

点 **「导出 WEM 音频（约 10GB）」**。

- 从每个 `WwiseWemResource` 取出原始字节：streaming 的按 `StreamingDataSource` 的 Locator 从 package 文件读，内嵌的读 `WemData`
- 实测抽出的字节与已有 `.wem` **逐字节相同**（头部为 `RIFF....WAVEfmt `）
- **体积约 10 GB**，点按钮时会弹窗告知并询问保存目录（「是」= 用配置里的默认目录，「否」= 选一个目录，「取消」= 不导出）
- 同样支持增量：目标目录里已存在的 `.wem` 会跳过

### 分析未使用的 WEM（可选）

点 **「分析未使用的 WEM」**。程序会：

- 读取 `missing_wem_files.csv`（未被使用的 WEM 列表）
- 解析 `banks.xml`，查这些 WEM 在哪些 bank 里
- 解析 `WwiseID` 目录找对应的 WwiseID
- 解析 `WemResJson` 目录找原始的 WwiseWemResource
- 生成 `unused_wem_with_banks.csv`，格式见 [unused_wem_with_banks.csv 的格式](#unused_wem_with_bankscsv-的格式)

需要先设置好 **Streaming WEM 目录**，否则程序会**直接报错**，而不是写一堆空路径出来。

### 标注音频

切到 **「标注音频」** 标签页，点 **「打开CSV...」** 载入 `unused_wem_with_banks.csv`。这一页提供：

- 左侧文件列表；标注会**自动写回你打开的那个 CSV**
- 实时 WEM 音频预览（vgmstream 解码 + WASAPI 播放）
- **一键播放该 WEM 所属的 txtp**：选中条目后点「播放所属txtp」或按 `Ctrl+T`，程序自动反查引用该 WemID 的 txtp 并播放，**无需手动拖入**；被多个 txtp 引用时会弹选择框
- **「打开音频目录」**：直接定位到当前选中 WEM 所在的文件夹
- 波形可视化，可点击跳转
- **「导出标注CSV...」** 另存一份标注结果；**「导出已标注WAV...」** 批量导出已标注的 WAV（以标注内容为文件名）
- 中英文界面切换

### 其他按钮

| 按钮 | 作用 |
| --- | --- |
| 重建 txtp 索引 | txtp 目录变动后重建 WemID → txtp 的索引 |
| 按 Event ID 导出 | 输入 Event ID，直接导出它引用的音频（用 `wem_map_cache.json` 缓存） |

## 路径设置

「提取音频」页顶部的路径**都可以直接手输**，改完立即写回 `config.json`，不必非得点「浏览...」。

| 路径 | 说明 |
| --- | --- |
| 项目根目录 | 其余目录都由它推导 |
| 音频导出目录 | ④ 的 WAV 输出位置 |
| Streaming WEM 目录 | `.wem` 文件所在目录（`WemPath` 列靠它） |
| txtp 目录 | 默认 `<项目根>\Extracted_Banks\txtp` |
| vgmstream | 手动指定优先，否则用 exe 旁 `utils\` 里的 |
| 游戏根目录 | ⓪ 的输入，含 `DS2.exe` 的那一层 |
| WEM 音频目录 | 「导出 WEM 音频」的输出位置 |

- **「浏览...」** 用的是 .NET 8+ 的 `OpenFolderDialog`，也就是标准资源管理器式对话框（带地址栏、导航窗格、搜索框），不是旧版树形框
- **「自动查找」** 用于游戏根目录
- **项目根目录会自动探测**：目录下存在 `GraphSoundRes` / `BankRes` / `WemResJson` / `Extracted_Banks` 之一即算命中；探测不到时程序会让你选一次，之后记住

## unused_wem_with_banks.csv 的格式

基础列（**已去掉无信息量的 `IsStreaming`**）：

```
WemID,Coord,JsonFile,WemFile,WemPath,FoundInBankRes,TxtpFiles
```

| 字段 | 说明 |
| --- | --- |
| WemID | WEM 文件的 ID |
| Coord | WemRes 的坐标（如 `1604:4570`） |
| JsonFile | 对应的 WwiseWemResource JSON 文件名 |
| WemFile | WEM 文件名 |
| WemPath | WEM 文件的完整路径（依赖 **Streaming WEM 目录** 设置正确） |
| FoundInBankRes | 是否被 BankRes 的 `WemIDs` 引用（是 / 否） |
| TxtpFiles | 引用该 WEM 的 txtp 文件名，多个用 `;` 分隔 |

重新生成时不会丢信息：

- 旧文件里**除基础列与被删列（`IsStreaming`）以外的所有列**，都按 WemID 原样带过来 —— `Label` / `Duration` / `Channel`，或你自己加的任意列
- 旧文件里**已经不再是「未使用」的行**，会整行**带到文件末尾**，连同它的标注一起保留
- 日志里会提示这两类各带过来多少

## 原 Python 脚本（参考实现）

`pyscript/` 下保留着 7 个脚本，本项目最初就是靠它们做的。它们读写与 WemLabeler 完全相同的文件格式，所以可以混用；但**已不是主路径**，下面的按钮才是。

| WemLabeler 按钮 | 等价脚本 |
| --- | --- |
| ⓪ 导出资源（直读游戏文件） | —（新增，取代原来手工开 Odradek 导出） |
| ① 从 BankRes 提取 BNK | `extract_bnk_from_json.py` |
| ② 用 wwiser 生成 TXTP | 原来手工开 wwiser 点「Generate TXTP」 |
| ③ 构建音频映射表 | `export_sounds.py 1` |
| ④ 按映射表导出音频 | `export_sounds.py 2` |
| 按 Event ID 导出 | `export_by_id.py` |
| 分析未使用的 WEM | `link_unused_wem.py` |

其余脚本：`build_audio_manifest.py`（构建音频清单）、`fix_negative_ids.py`（修 JSON 里的负数 ID）、`match.py`（按音频内容 MD5 找回原文件名）。

> 这些脚本里仍写有硬编码路径（例如 `BASE_DIR`），如果要用请自行修改。它们在项目根目录下运行，例如
> `cd <项目根>` 后 `python pyscript\extract_bnk_from_json.py`（`extract_bnk_from_json.py` 用的是相对路径 `./BankRes`）。

## 项目结构

```
Odradek-wwise-namer/
├── OdradekSharp/            # 直读 Decima 游戏文件的 C# 读取器（对 Odradek 的逐行移植）
│   ├── Data/                # types.json (8.24 MB) + extensions.json —— 类型模式，运行期必需
│   ├── Ds2/                 # 流式图、对象读取、门面 (DecimaGame)
│   ├── Rtti/                # 类型表、反序列化、14 个 DS2 回调
│   ├── Io/                  # DSAR 容器 + LZ4 + 小端读取 / murmur3 / CRC-32C
│   ├── Export/              # 与 Odradek 逐字节相同的 JSON 导出
│   └── Program.cs           # 独立 CLI（info / types / find / read / dump / hex），调试用
├── WemLabeler/              # WPF 主程序：提取音频 + 标注音频
│   ├── MainWindow.xaml      # 两个标签页的布局
│   ├── MainWindow.xaml.cs   # 标注页逻辑（播放 / 标注 / 导出）
│   ├── MainWindow.Extract.cs# 提取页逻辑（流水线、资源导出、WEM 抽取）
│   ├── TxtpPickerWindow.xaml# 多个候选 txtp 时的选择窗口
│   ├── WemEntry.cs          # 数据模型
│   ├── Locale.cs            # 国际化
│   ├── ConfigManager.cs     # 配置读写
│   ├── Program.cs           # 入口（WPF）
│   ├── locales/             # 语言文件 (zh-CN / en-US)
│   ├── Pipeline/            # 音频流水线（由 Python 迁移而来）
│   │   ├── OdradekExporter.cs       # ⓪ 直读游戏文件导出 + WEM 音频抽取
│   │   ├── AudioPipeline.cs         # BNK 提取 + 各索引构建
│   │   ├── AudioPipeline.Mapping.cs # 映射表构建
│   │   ├── AudioPipeline.Export.cs  # 音频导出 / 按 ID 导出 / 未使用 WEM
│   │   ├── WwiserRunner.cs          # 调 wwiser 生成 TXTP
│   │   ├── ToolLocator.cs           # 定位与下载 vgmstream / wwiser
│   │   ├── TxtpRepository.cs        # txtp 索引：WemID → 所属 txtp
│   │   ├── PipelinePaths.cs         # 目录结构解析与自动探测
│   │   └── PipelineModels.cs        # 数据模型
│   └── README.md            # WemLabeler 详细使用说明
├── pyscript/                # 原 Python 脚本（参考实现，非主路径）
├── Extracted_Banks/         # ① 产出的 .bnk；txtp/ 是 ② 的产出；banks.xml 由 wwiser 生成
├── WemResJson/              # ⓪ 产出的 WwiseWemResource JSON
├── BankRes/                 # ⓪ 产出的 WwiseBankResource JSON
├── GraphSoundRes/           # ⓪ 产出的 GraphSoundResource JSON
├── GraphPgmRes/             # ⓪ 产出的 GraphProgramResource JSON
├── NodeConstRes/            # ⓪ 产出的 NodeConstantsResource JSON
├── WwiseID/                 # ⓪ 产出的 WwiseID JSON
├── utils/                   # 只放 two_repos.txt（外部工具不在这里，见「工具会自动下载到哪里」）
├── .github/workflows/build.yml  # CI：单文件发布 win-x64
└── README.md                # 本文件
```

`Exported_Audio/`（④ 的输出）与 `WemResWem/`（Streaming WEM，默认位置）都**由项目根目录推导**，
不在上面的树里，因为它们的位置可以在路径设置里改到别处（例如放到空间更大的盘）。

## 输出文件说明

### 来自 ⓪ 导出资源

| 文件 | 说明 |
| --- | --- |
| `WemResJson\WwiseWemResource_*.json` | WwiseWemResource（另见「导出 WEM 音频」） |
| `BankRes\WwiseBankResource_*.json` | WwiseBankResource |
| `GraphSoundRes\GraphSoundResource_*.json` | GraphSoundResource |
| `GraphPgmRes\GraphProgramResource_*.json` | GraphProgramResource |
| `NodeConstRes\NodeConstantsResource_*.json` | NodeConstantsResource |
| `WwiseID\WwiseID_*.json` | WwiseID |

### 来自 ① ② ③ ④

| 文件 | 说明 |
| --- | --- |
| `Extracted_Banks\*.bnk` | ① 提取的 Wwise Bank |
| `Extracted_Banks\txtp\*.txtp` | ② wwiser 生成的 TXTP |
| `sound_wem_mapping_export.json` | ③ 音频映射表，含所有资源与音频源的关联 |
| `missing_wem_files.csv` | ③ 未被使用的 WEM 文件列表（补集） |
| `streaming_wem_map.csv` | ③ 缺失的 Streaming WEM 记录 |
| `mapping_build.log` | ③ 构建过程的详细日志 |
| `export_progress.json` | ④ 导出进度，支持断点续传 |
| `Exported_Audio\*.wav` | ④ 导出的 WAV（默认位置） |

### 来自 分析未使用的 WEM / 按 Event ID 导出

| 文件 | 说明 |
| --- | --- |
| `unused_wem_with_banks.csv` | 未被使用的 WEM 的完整来源信息（格式见上） |
| `wem_map_cache.json` | 「按 Event ID 导出」使用的 WEM 索引缓存 |

### 来自 WemLabeler 标注页

标注结果**自动写回你打开的那个 CSV**（追加/更新 `Label`、`Duration`、`Channel` 列），
不会另生成固定文件名的副本；需要另存时用 **「导出标注CSV...」** 自己选路径。

| 文件 | 说明 |
| --- | --- |
| `config.json` | 工具配置（各路径、语言、vgmstream 路径等），位于 exe 旁 |
| `logs\vgmstream_YYYYMMDD.log` | 启动与解码日志 |

## 数据规模参考

| 项 | 规模 |
| --- | --- |
| `Extracted_Banks` | 74 个 `.bnk`，约 1 GB |
| `Extracted_Banks\banks.xml` | 515 MB |
| `Extracted_Banks\txtp` | 约 11,715 个文件 |
| `unused_wem_with_banks.csv` | 5,951 行 |
| WEM 原始音频 | 约 10 GB |

## 构建

```bash
dotnet build WemLabeler/WemLabeler.csproj
```

CI 见 [.github/workflows/build.yml](.github/workflows/build.yml)：push 到 `WemLabeler/**` 时，
用 .NET 10 做单文件发布（win-x64，非自包含）并上传产物。

> **发布产物必须包含 `Data\types.json`**（以及 `extensions.json`）。
> `WemLabeler.csproj` 已经把 `OdradekSharp/Data/` 里的这两个文件带到输出目录，改动工程文件时别把它去掉。

### 为什么 `OdradekSharp/Data/types.json` 要入库

它是 Odradek 自己仓库里 `odradek-game-ds2/src/main/resources/types.json` 的**逐字节拷贝**，
**不是任何构建步骤生成的**：里面定义了每个类的字段、偏移和读取顺序，没有它一个字节都解析不了。

所以它必须提交（`utils/` 是 gitignored，CI 检出后没有别的途径拿到它）。
文件在工作区是 8.24 MB，但 git 用 zlib 存储，**实际只占约 1 MB**。

## 已知限制

- **未移植的回调**：含 `PhysicsShapeResource` / `PhysicsRagdollResource`（Jolt）与
  `FacialRigSettingWithLODResource`（RigLogic）的组，因为回调未移植会**整组读取失败**。
  现在有兜底：**降级为「不读子组」再试一次**，目标对象仍能导出，代价是这些子组里的指针解析不到（会退化成未解析的 `<ref>`）。
- **派生类型默认不导出**：`WwiseWemLocalizedResource`（268 个对象，是 `WwiseWemResource` 的派生类型）
  默认**不导出**（`OdradekExporter.IncludeDerivedTypes = false`）。原因有两个：一是与原 Odradek 的导出结果一致，
  二是那些本地化大组读起来要吃几 GB 内存。需要的话要同时改 pipeline 里 `WwiseWemResource_*.json` 的匹配。
- **首次全量导出较慢**：没有任何已有文件时要顺序读约 2,600 个组（这正是当初在 Odradek 界面里手工导出所做的工作量）。
  已按组做 GC 回收，但仍建议留出时间。
- **游戏版本变化**：类型与对象索引由游戏本体决定，游戏更新后请重新跑 ⓪。

## 注意事项

- ⓪ 与「导出 WEM 音频」都依赖正确的**游戏根目录**（含 `DS2.exe`）
- `WemPath` 与「导出 WEM 音频」都依赖正确的 **Streaming WEM 目录**；该目录不存在时程序会直接报错
- WEM 音频约 10 GB，导出前请确认目标盘空间
- 导出过程可能很久，③ 与 ④ 都支持断点续传
- WemLabeler 的「提取音频」页与 Python 脚本产物一致（已逐字节/逐行校验），两者可以交替使用

## 致谢

- [ShadelessFox](https://github.com/ShadelessFox) - 创建 Odradek 和 Decima Workshop
- [bnnm](https://github.com/bnnm) - 创建 wwiser 工具
- [vgmstream 团队](https://github.com/vgmstream/vgmstream) - 提供游戏音频转换工具
