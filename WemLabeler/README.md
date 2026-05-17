# WEM Labeler

WPF 桌面工具，用于对 [unused_wem_with_banks.csv](../unused_wem_with_banks.csv) 中未命名的 WEM 音频文件进行**文本标注**。支持通过 vgmstream 实时解码预览音频，波形可视化，以及批量导出带标签的 WAV 文件。

## 功能概览

- **CSV 加载**：解析 10 列 `unused_wem_with_banks.csv`，自动识别列名，支持含引号的 CSV 字段
- **实时音频预览**：通过 vgmstream-cli.exe 将 WEM 解码为 WAV → NAudio WASAPI 播放，纯软件流水线，不依赖系统解码器
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
- **键盘快捷键**：← → 切换文件 / Space 播放停止 / Ctrl+S 保存标注 / Enter 在标注框内保存
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
┌──────────────────────────────────────────────────┐
│  文件  帮助                                       │
├──────────────┬─────┬──────────────────────────────┤
│ ✓  WemID     │     │  文件信息                    │
│    Filename  │     │  WemID / 路径                │
│    Label     │ 分  │  来源关联                    │
│              │     │  WemRes / Banks / 大小       │
│  文件列表    │ 隔  │                              │
│  (左侧)     │     │  [播放] [停止] [自动播放]     │
│              │ 条  │  ┌──────────────────────┐    │
│              │     │  │   波形图 (Canvas)     │    │
│              │     │  └──────────────────────┘    │
│              │     │  标注内容: [__________]      │
│              │     │  [保存] [上一个] [下一个]    │
│              │     │  进度: 12 / 50               │
├──────────────┴─────┴──────────────────────────────┤
│  状态栏                                           │
└──────────────────────────────────────────────────┘
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
| Ctrl+S | 保存当前标注 |
| Enter | 在标注框中按下即保存 |

### 导出 WAV

通过菜单 **文件 → 导出已标注 WAV**，选择目标文件夹。程序会自动：

1. 遍历所有已标注的文件
2. 使用 vgmstream 将每个 WEM 解码为 WAV
3. 以**标注内容**作为文件名保存

如果某个标注在同目录下产生文件名冲突，会自动追加 WemID 作为后缀。

## 配置文件

首次运行后，程序会在所在目录生成 `config.json`：

```json
{
  "LastCsvPath": "D:\\Odradek-wwise-namer\\unused_wem_with_banks.csv",
  "VgmstreamPath": "E:\\vgmstream\\vgmstream-cli.exe",
  "LastExportPath": "D:\\Odradek-wwise-namer\\labeled_wem_files.csv",
  "AutoPlay": false,
  "Language": "zh-CN"
}
```

| 字段 | 说明 |
|------|------|
| LastCsvPath | 上次打开的 CSV 路径（启动时可选择恢复） |
| VgmstreamPath | vgmstream-cli.exe 的路径 |
| LastExportPath | 标注导出的默认路径 |
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

## 技术栈

- **.NET 10.0** (WPF)
- **NAudio 2.3.0** — 音频播放与处理
- **vgmstream-cli** — WEM 解码引擎
- **System.Text.Json** — 配置与国际化文件解析

## 许可

本项目仅供学习和研究使用。
