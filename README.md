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
- PowerShell

## 使用步骤

### 步骤 1：使用 Odradek 导出资源

使用 Odradek 从 Death Stranding 2 中导出以下资源（均为 JSON 格式）：

| 资源类型                  | 导出格式       | 导出路径                                   |
| --------------------- | ---------- | -------------------------------------- |
| WwiseWemResource      | `.wem` 文件  | `d:\Odradek-wwise-namer\WemNamer\WEM`  |
| WwiseWemResource      | `.json` 文件 | `d:\Odradek-wwise-namer\WemRes`        |
| WwiseBankResource     | `.json` 文件 | `d:\Odradek-wwise-namer\BankRes`       |
| GraphSoundResource    | `.json` 文件 | `d:\Odradek-wwise-namer\GraphSoundRes` |
| GraphProgramResource  | `.json` 文件 | `d:\Odradek-wwise-namer\GraphPgmRes`   |
| NodeConstantsResource | `.json` 文件 | `d:\Odradek-wwise-namer\NodeConstRes`  |
| WwiseID               | `.json` 文件 | `d:\Odradek-wwise-namer\WwiseID`       |

### 步骤 2：运行重命名和提取脚本

#### 2.1 重命名 WEM 文件

运行 PowerShell 脚本，根据 JSON 中的 WemID 重命名 WEM 文件：

```powershell
cd d:\Odradek-wwise-namer\WemNamer
.\jsonrename.ps1
```

此脚本会：

- 读取 `WemNamer\WEM` 中的 `.wem` 文件
- 根据 `WemRes` 中对应 JSON 的 `WemID` 重命名文件
- 将重命名后的文件移动到 `WemNamer\RENAMED` 目录

#### 2.2 提取 BNK 文件

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

#### 脚本配置说明

在 `export_sounds.py` 中，你可以修改以下配置：

```python
BASE_DIR = Path(r"D:\Odradek-wwise-namer")          # 项目根目录
OUTPUT_DIR = Path(r"G:\ds2 unpack\wems\Exported_Audio")  # 导出目录
VGMSTREAM_CLI = Path(r"E:\下载\odradek\vgmstream-r2083\vgmstream-cli.exe")  # vgmstream 路径
```

#### 两阶段处理

脚本支持两阶段处理，通过 `BUILD_MAPPING_ONLY` 开关控制：

- `BUILD_MAPPING_ONLY = True`：仅构建映射表，不导出音频
- `BUILD_MAPPING_ONLY = False`：基于已有映射表导出音频（如果映射表不存在会自动先构建）

## 项目结构

```
Odradek-wwise-namer/
├── WemNamer/
│   ├── WEM/              # 原始 WEM 文件（从 Odradek 导出）
│   ├── RENAMED/          # 重命名后的 WEM 文件
│   └── jsonrename.ps1    # WEM 重命名脚本
├── WemRes/               # WwiseWemResource JSON 文件
├── BankRes/              # WwiseBankResource JSON 文件
├── Extracted_Banks/      # 提取的 BNK 文件
│   └── txtp/             # wwiser 生成的 TXTP 文件
├── GraphSoundRes/        # GraphSoundResource JSON 文件
├── GraphPgmRes/          # GraphProgramResource JSON 文件
├── NodeConstRes/         # NodeConstantsResource JSON 文件
├── WwiseID/              # WwiseID JSON 文件
├── extract_bnk_from_json.py  # BNK 提取脚本
├── export_sounds.py      # 音频导出主脚本
└── README.md             # 本文件
```

## 注意事项

- 确保所有 JSON 资源文件正确导出，否则脚本可能无法找到对应的引用关系
- vgmstream 路径需要根据实际情况修改
- 导出过程可能需要较长时间，脚本支持断点续传（通过 `export_progress.json`）
- Streaming 类型的 WEM 文件需要在 `WemNamer\RENAMED` 中存在才能正确导出

## 致谢

- [ShadelessFox](https://github.com/ShadelessFox) - 创建 Odradek 和 Decima Workshop
- [bnnm](https://github.com/bnnm) - 创建 wwiser 工具
- [vgmstream 团队](https://github.com/vgmstream/vgmstream) - 提供游戏音频转换工具

