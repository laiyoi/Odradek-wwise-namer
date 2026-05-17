# Odradek-wwise-namer

<p align="center">
  <a href="README_EN.md">English Version</a>
</p>

基于 [Odradek](https://github.com/ShadelessFox/odradek) 的音频文件命名与导出工具，专为 **Death Stranding 2** 设计。

## 简介

[Odradek](https://github.com/ShadelessFox/odradek) 是 Horizon Forbidden West 的资源查看器和提取器，是 [Decima Workshop](https://github.com/ShadelessFox/decima) 的重生版本。它专为 Decima 引擎游戏的模组制作者设计，提供查看和提取游戏资源的能力。

本项目利用 Odradek 作为基础，实现 Death Stranding 2 音频资源的自动化命名与导出。

## 前置需求

- [Odradek](https://github.com/ShadelessFox/odradek) - 用于导出游戏资源
- [wwiser](https://github.com/bnnm/wwiser) - 用于解析 Wwise Bank 并生成 TXTP 文件
- [vgmstream](https://github.com/vgmstream/vgmstream) - 用于将 WEM 转换为 WAV
- Python 3.x

## 使用步骤

### 步骤 1：使用 Odradek 导出资源

使用 Odradek 从 Death Stranding 2 中导出以下资源（均为 JSON 格式）：

| 资源类型                  | 导出格式       | 导出路径                                   |
| --------------------- | ---------- | -------------------------------------- |
| WwiseWemResource      | `.wem` 文件  | `d:\Odradek-wwise-namer\WemResWem`     |
| WwiseWemResource      | `.json` 文件 | `d:\Odradek-wwise-namer\WemRes`        |
| WwiseBankResource     | `.json` 文件 | `d:\Odradek-wwise-namer\BankRes`       |
| GraphSoundResource    | `.json` 文件 | `d:\Odradek-wwise-namer\GraphSoundRes` |
| GraphProgramResource  | `.json` 文件 | `d:\Odradek-wwise-namer\GraphPgmRes`   |
| NodeConstantsResource | `.json` 文件 | `d:\Odradek-wwise-namer\NodeConstRes`  |
| WwiseID               | `.json` 文件 | `d:\Odradek-wwise-namer\WwiseID`       |

### 步骤 2：提取 BNK 文件

运行 Python 脚本，从 JSON 中提取 Wwise Bank 数据：

```bash
cd d:\Odradek-wwise-namer
python extract_bnk_from_json.py
```

此脚本会：

- 读取 `BankRes` 中的 JSON 文件
- 解码 Base64 编码的 BankData
- 修复 Wwise Bank 数据的对齐问题
- 将提取的 `.bnk` 文件保存到 `Extracted_Banks` 目录

### 步骤 3：使用 wwiser 生成 TXTP

使用 [wwiser](https://github.com/bnnm/wwiser) 读取所有 `Extracted_Banks` 目录下的 `.bnk` 文件，生成 `.txtp` 文件。

1. 打开 wwiser（双击 `wwiser.pyz`）
2. 点击 **Load dirs...**，选择 `d:\Odradek-wwise-namer\Extracted_Banks` 目录
3. 点击 **Generate TXTP** 生成 `.txtp` 文件
4. 确保 `.txtp` 文件生成在 `Extracted_Banks\txtp` 目录中

### 步骤 4：运行导出脚本

```bash
cd d:\Odradek-wwise-namer
python export_sounds.py
```

此脚本会：

- 构建音频映射表（Mapping），关联 GraphSoundResource、GraphProgramResource、NodeConstantsResource 和 WwiseID
- 根据 TXTP 文件解析音频源（Embedded 或 Streaming）
- 使用 vgmstream 将音频导出为 WAV 格式
- 生成 `streaming_wem_map.csv` 记录缺失的 Streaming WEM 文件
- 生成 `missing_wem_files.csv` 记录未被使用的 WEM 文件（补集）

### 步骤 5：分析未使用的 WEM 文件（可选）

运行 `link_unused_wem.py` 脚本，分析未被使用的 WEM 文件的来源：

```bash
cd d:\Odradek-wwise-namer
python link_unused_wem.py
```

此脚本会：

- 读取 `missing_wem_files.csv`（未被使用的 WEM 文件列表）
- 解析 `banks.xml`，查找这些 WEM 在哪些 bank 中
- 解析 `WwiseID` 目录，查找对应的 WwiseID
- 解析 `WemRes` 目录，查找原始的 WwiseWemResource 文件
- 生成 `unused_wem_with_banks.csv`，包含完整的关联信息

**输出格式**：

| 字段 | 说明 |
|------|------|
| WemID | WEM 文件的 ID |
| FoundInWemRes | 是否在 WemRes 中找到 |
| WemResCoord | WemRes 的坐标（如 `2:878`） |
| FoundInBanks | 是否在 banks.xml 中找到 |
| Banks | 所在的 bank 文件列表 |
| BankIDs | bank 的 dwSoundBankID |
| FoundInWwiseID | 是否在 WwiseID 中找到 |
| WwiseIDCoord | WwiseID 的坐标 |

#### 脚本配置说明

在 `export_sounds.py` 中，你可以修改以下配置：

```python
BASE_DIR = Path(r"D:\Odradek-wwise-namer")          # 项目根目录
WEM_RES_WEM_DIR = BASE_DIR / "WemResWem"             # Streaming WEM 文件（导出为 .wem）
WEM_RES_DIR = BASE_DIR / "WemRes"                    # WwiseWemResource JSON 文件
OUTPUT_DIR = Path(r"G:\ds2 unpack\wems\Exported_Audio")  # 导出目录
VGMSTREAM_CLI = Path(r"E:\下载\odradek\vgmstream-r2083\vgmstream-cli.exe")  # vgmstream 路径
```

#### 命令行参数

脚本支持命令行参数控制处理模式：

```bash
# 阶段一：仅构建映射表
python export_sounds.py 1

# 阶段二：基于已有映射表导出音频
python export_sounds.py 2

# 先构建映射表，再导出音频（一键完成）
python export_sounds.py 12
```

不带参数运行时，脚本会进入**交互式模式**，提示你选择操作。

#### 两阶段处理说明

脚本采用两阶段处理架构，提高效率：

- **阶段一（Build Mapping）**：解析所有 JSON 和 TXTP 文件，生成 `sound_wem_mapping_export.json`
  - 只读不写，速度极快
  - 生成详细的映射表，包含所有音频源信息
  - 记录缺失的 WEM 文件到 `missing_wem_files.csv`

- **阶段二（Export）**：基于映射表直接导出音频
  - 跳过重复的 JSON 解析
  - 专注音频导出，支持断点续传
  - 自动记录导出进度到 `export_progress.json`

## 项目结构

```
Odradek-wwise-namer/
├── WemResWem/            # WwiseWemResource .wem 文件（按坐标命名）
├── WemRes/               # WwiseWemResource JSON 文件
├── BankRes/              # WwiseBankResource JSON 文件
├── Extracted_Banks/      # 提取的 BNK 文件
│   ├── txtp/             # wwiser 生成的 TXTP 文件
│   └── banks.xml         # wwiser 生成的 bank 信息
├── GraphSoundRes/        # GraphSoundResource JSON 文件
├── GraphPgmRes/          # GraphProgramResource JSON 文件
├── NodeConstRes/         # NodeConstantsResource JSON 文件
├── WwiseID/              # WwiseID JSON 文件
├── extract_bnk_from_json.py  # BNK 提取脚本
├── export_sounds.py      # 音频导出主脚本
├── export_by_id.py       # 按 ID 导出指定音频
├── build_audio_manifest.py   # 构建音频资源清单
├── fix_negative_ids.py   # 修复 JSON 中的负数 ID
├── link_unused_wem.py    # 分析未使用的 WEM 文件来源
└── README.md             # 本文件
```

## 输出文件说明

脚本运行后会生成以下文件：

### 来自 `export_sounds.py`
| 文件 | 说明 |
|------|------|
| `sound_wem_mapping_export.json` | 音频映射表，包含所有资源和音频源的关联信息 |
| `export_progress.json` | 导出进度，支持断点续传 |
| `streaming_wem_map.csv` | 缺失的 Streaming WEM 文件记录 |
| `missing_wem_files.csv` | **未被使用的 WEM 文件列表**（补集） |
| `mapping_build.log` | Mapping 构建过程的详细日志 |

### 来自 `link_unused_wem.py`
| 文件 | 说明 |
|------|------|
| `unused_wem_with_banks.csv` | 未被使用的 WEM 文件的完整来源信息，包含 WemRes 坐标、bank 信息和 WwiseID 关联 |

## 注意事项

- 确保所有 JSON 资源文件正确导出，否则脚本可能无法找到对应的引用关系
- vgmstream 路径需要根据实际情况修改
- 导出过程可能需要较长时间，脚本支持断点续传（通过 `export_progress.json`）
- Streaming 类型的 WEM 文件需要在 `WemResWem` 目录中（由 Odradek 导出），文件名格式为 `WwiseWemResource_{group}_{index}.wem`
- 如果某些 WEM 文件缺失，可以查看 `missing_wem_files.csv` 了解详情

## 致谢

- [ShadelessFox](https://github.com/ShadelessFox) - 创建 Odradek 和 Decima Workshop
- [bnnm](https://github.com/bnnm) - 创建 wwiser 工具
- [vgmstream 团队](https://github.com/vgmstream/vgmstream) - 提供游戏音频转换工具
