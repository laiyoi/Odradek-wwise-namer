# pyscript —— Python 阶段的原始脚本

这里是这个项目最早的实现：一组互相独立的 Python 脚本，靠**中间 JSON** 串起
「odradek 导出资源 → 提 bank → 生成 txtp → 建映射 → 导音频」这条链。

后来整条流水线已经用 C# 重写进 [WemLabeler](../WemLabeler/)（GUI + 直读游戏文件），
**本目录的脚本已经不再是流水线的一部分**，保留下来是为了：

- 当参考实现读（逻辑、字段名、边界情况都在这里）；
- 需要脱离 GUI、单独跑某一步时的应急手段；
- 与 C# 版本对拍。

> ⚠️ **大多数脚本现在不能直接原样跑**：它们依赖的资源 JSON 目录（`BankRes` / `GraphSoundRes` /
> `GraphPgmRes` / `NodeConstRes` / `WwiseID` / `WemResJson`）已经不再产出，见下节。

---

## 1. 与当前流水线的对应关系

C# 版的每一步都能在 GUI 的「提取音频」页找到同名按钮，这里列出迁移关系：

| 脚本 | 原来的作用 | C# 对应 | 现状 |
| ---- | ---------- | ------- | ---- |
| （odradek.exe 导出） | 按类型导出 6 类资源 JSON | **⓪ 读游戏资源**（`Pipeline/OdradekExporter.cs`） | **已被取代**：直读 `streaming_graph.core`，不再落任何按对象的 JSON |
| `extract_bnk_from_json.py` | 从 `BankRes/*.json` 的 Base64 `BankData` 还原 `.bnk` | 并入 **⓪** | **已被取代**：⓪ 直接把 `WwiseBankResource.BankData` 写成 `.bnk` |
| `export_sounds.py`（阶段一） | 读资源 JSON 走 `GraphSound → GraphProgram → NodeConstants → WwiseID`，再配 txtp 生成映射表 | **⓪ + ③**（`Pipeline/SoundChainResolver.cs`、`Pipeline/AudioPipeline.Mapping.cs`） | **已被取代**：链路直读游戏数据，③ 只做 txtp 富化 |
| `export_sounds.py`（阶段二） | 按映射表导出 WAV（断点续传、streaming 缺失记录） | **④ 按映射表导出音频**（`Pipeline/AudioPipeline.Export.cs`） | 已迁移；行为一致 |
| `export_by_id.py` | 按 Event ID 导出，带 `wem_map_cache.json` | **按 Event ID 导出** | 已迁移 |
| `link_unused_wem.py` | 交叉引用 WemResJson / BankRes / txtp 找未使用的 WEM | **分析未使用的 WEM** | 已迁移；数据源改为映射表 + `wem_index.json` |
| `build_audio_manifest.py` | 用 FNV-1 算 `WwiseID_Calc`，产出 `DS2_Audio_Master_Index.csv` | —— | **仅参考**：一份独立的分析产物，没进流水线 |
| `fix_negative_ids.py` | 把 JSON 里的负数 ID 就地改写成无符号 | —— | **仅参考**：C# 侧在读取时就用 `& 0xFFFFFFFF` 处理，不需要事后修文件 |
| `match.py` | 按 WAV 的 data chunk 大小做配对/查重 | —— | **仅参考**：与音频导出无关的独立小工具 |

---

## 2. 为什么不能直接再跑

两个破坏性变化：

1. **中间 JSON 目录全部取消。** 以前 ⓪（odradek 导出）会产出六个目录：

   | 目录 | 内容 |
   | ---- | ---- |
   | `GraphSoundRes/` | 5,700 个 GraphSoundResource |
   | `GraphPgmRes/` | 25,663 个 GraphProgramResource |
   | `NodeConstRes/` | 26,385 个 NodeConstantsResource |
   | `WwiseID/` | 7,000 个 WwiseID |
   | `WemResJson/` | 7,838 个 WwiseWemResource |
   | `BankRes/` | 74 个 WwiseBankResource（含 Base64 的 BankData） |

   现在只剩两类中间产物：**`Extracted_Banks/`**（`.bnk` + `txtp/`）与 **`WemResWem/`**（`.wem`）。
   所以 `export_sounds.py` 的阶段一、`extract_bnk_from_json.py`、`link_unused_wem.py`、
   `export_by_id.py` 里读这些目录的代码都会找不到文件。

2. **`sound_wem_mapping_export.json` 的字段变了。** 只保留
   `ResourceName` / `GraphSound` / `WwiseID` / `TXTP_Filename` / `AudioSources`，
   丢掉了 `GraphProgram` / `ExposedDataResource` / `WwiseID_Coord`；
   `WwiseID_Value` 改名成 `WwiseID`。
   `AudioSources[].WemRes_Coord` 也从错的 `WemID:<数字>` 改成了真正的对象坐标 `组:下标`。

   要拿这些脚本当参考的话，注意按新字段名改。

---

## 3. 各脚本速查

### `export_sounds.py`

最早的「阶段一 + 阶段二」一体脚本，直接跑会进交互菜单（`1` / `2` / `12` / `q`）。

- 阶段一：`GraphSoundRes/*.json` → `GraphProgramResource_..json` → `NodeConstantsResource_..json`
  → `WwiseID_*.json`，再查 `Extracted_Banks/txtp/` 里 `CAkEvent[<id>] <eventId>` 建事件索引，
  产出 `sound_wem_mapping_export.json`、`missing_wem_files.csv`、`mapping_build.log`。
- 阶段二：按映射表逐个音频源调 vgmstream 导 WAV，用 `export_progress.json` 断点续传，
  streaming 缺失的写进 `streaming_wem_map.csv`。

坑（C# 版已逐个修掉，读代码时留意）：

- 事件索引是「后写覆盖」的，同一个 event ID 命中多个 `.txtp` 时谁胜出取决于目录枚举顺序；
- 内嵌音频行只 `replace("../", "", 1)`，只剥一层 `../`；
- 没有对 `DefaultSoftLinkedObjects` 做类型判断，靠「JSON 文件存不存在」来筛 WwiseID。

### `extract_bnk_from_json.py`

读 `./BankRes/*.json` 的 `BankData`（Base64），Base64 解码 → `fix_wwise_data`
（按最后一个 Wwise chunk 声明长度补齐/截断）→ 写 `Extracted_Banks/*.bnk`。

> C# 的 `AudioPipeline.FixWwiseBankData` 就是这段逻辑的移植，⓪ 直接对游戏里读到的
> `BankData` 字节跑同一个修复，实测 74/74 与旧路径逐字节相同。

### `export_by_id.py`

`WemResJson` 建 `WemID → .wem 路径` 索引（缓存到 `wem_map_cache.json`），
按指定 Event ID 导出到 `Decoded_Audio_Split/`。

### `link_unused_wem.py`

`WemResJson` + `BankRes(WemIDs)` + `Extracted_Banks/txtp/` + `WemResWem` 四方交叉，
把**没有被任何 txtp 用到**的 WEM 写进 `unused_wem_with_banks.csv`。
产出 CSV 的列：`WemID,Coord,JsonFile,WemFile,WemPath,FoundInBankRes,TxtpFiles`
（C# 版还会把它读回来、把人工填的额外列按 WemID 原样带回去；只丢弃 `IsStreaming`）。

### `build_audio_manifest.py`

对每个 GraphSound 的 `ResourceName` 算 FNV-1 32 位哈希当 `WwiseID_Calc`，
串起名字 / WemID / Locator / Offset / Length / 时长，产出 `DS2_Audio_Master_Index.csv`。

### `fix_negative_ids.py`

遍历目录里的 JSON，把 `WemID` / `WwiseID` / `ResourceNameHash` 等字段的负数
加 `2^32` 修正成无符号，就地覆写。

### `match.py`

命令行小工具：读 WAV 的 `data` chunk 大小，按大小做匹配/查重。

---

## 4. 运行环境里的硬编码路径

这些脚本当初是按本机布局写死路径的，换机器要改：

| 脚本 | 写死的路径 |
| ---- | ---------- |
| `export_sounds.py` | `BASE_DIR = E:\Odradek-wwise-namer`、`WEM_RES_WEM_DIR = G:\ds2_unpack\wems\WemResWem`、`OUTPUT_DIR`、`VGMSTREAM_CLI = D:\下载\odradek\...\vgmstream-cli.exe` |
| `export_by_id.py` | 同上，外加 `WEM_RES_WEM_DIR` 里那行 `G:\...` 少了 `r` 前缀，是个 `\U` 转义隐患 |
| `link_unused_wem.py` | `BASE_DIR`、`WEM_RES_WEM_DIR` |

---

## 5. 现在要跑 wwiser 的话

`wwiser.pyz` 是 Python zipapp，**必须由 Python 解释器运行**（官方 release 只发
`wwiser.pyz` + `wwnames.db3`，没有免 Python 的构建）。

### 5.1 本目录的 Python 脚本：wwiser 放**项目根**

这些脚本是围绕 `BASE_DIR = <项目根>` 写死的，手动跑 wwiser 也一直是这个布局：

| 东西 | 位置 |
| ---- | ---- |
| `wwiser.pyz` | `<项目根>\wwiser.pyz` |
| `wwnames.db3` | `<项目根>\wwnames.db3`（**必须和 pyz 同目录**） |
| `wwiser.ini` | `<项目根>\wwiser.ini`（wwiser 自己记的 `last_path`） |

```bat
cd /d "<项目根>"
python wwiser.pyz -g -go "Extracted_Banks/txtp" "Extracted_Banks/*.bnk"
```

> **`wwnames.db3` 是关键。** 它是 wwiser 的 hash→名字库（SQLite，481 条），例如
> `782826392 → default`、`964811743 → Lv2`。有它，txtp 名是 `[3984055919=Lv2]`；
> **没它**就退化成 `[3984055919=964811743]`。两次运行的输出目录不能混用 ——
> wwiser 从不删旧文件，两套名字同时存在时文件数会变成并集
> （实测两次分别 11,718 / 11,710，其中 1,117 与 1,109 个名字对不上）。

### 5.2 WemLabeler（C#）：工具都在 exe 的 `utils`

C# 用的是它自己的一套，**放在 exe 旁边，和项目根无关**：

| 东西 | 位置 |
| ---- | ---- |
| `wwiser.pyz`、`wwnames.db3` | `<exe目录>\utils\` |
| 嵌入式 Python | `<exe目录>\utils\python\python.exe` |
| 启动脚本（绕开 tkinter） | `<exe目录>\utils\wwiser_cli.py` |

「下载工具」按钮会把这三样自动装好。工作目录用的是 `Extracted_Banks`、`-go` 给相对路径，
所以 txtp 里写出的是干净的 `../WwiseBankResource_x_y.bnk`。

```bat
cd /d "<项目根>\Extracted_Banks"
"<exe目录>\utils\python\python.exe" "<exe目录>\utils\wwiser_cli.py" "<exe目录>\utils\wwiser.pyz" ^
    -g -go "txtp" "*.bnk"
```

**为什么 C# 这边要多一个启动脚本**：`wwiser.pyz` 的 `__main__.py` 会**无条件**
`import wwiser.wgui`，而 `wgui` 需要 `tkinter`；python.org 的 embeddable 包**不含 tkinter**，
直接跑会 `ModuleNotFoundError: No module named 'tkinter'`，哪怕只是命令行用法。
zipapp 本质是个 zip，`sys.path` 里加进去就能直接 `import wwiser.wcli`，于是完全不需要 GUI 那一支。

> 两个布局各用各的文件，互不干扰：项目根那份给你手动跑，`utils\` 那份给程序用。

---

## 6. 许可

与仓库其余部分一致，见 [../LICENSE](../LICENSE)。
