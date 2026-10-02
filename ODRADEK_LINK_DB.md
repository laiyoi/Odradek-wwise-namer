# odradek 链接数据库（`links-*.db`）格式与实测说明

> 目的：把这份数据库讲到「另一个 agent 不用看 Java 源码、只靠本文档就能完整解析并正确使用」。
> 参考实现见 [utils/odradek_links.py](utils/odradek_links.py)（本文档所有统计都由它复现）。
> 上游源码：`odradek-app/src/main/java/sh/adelessfox/odradek/app/util/LinkDatabase.java`（`FILE_VERSION = 1`）。

---

## 1. 样本文件（本文所有数字都来自这一份）

| 项 | 值 |
|---|---|
| 路径 | `C:\Users\laiyoi\AppData\Local\Odradek\links-3b11a80e0c3b582e808cf07fc8266ddc.db` |
| 大小 | 147,420,326 字节（140.6 MiB） |
| 修改时间 | 2026-06-16 20:59 |
| SHA-256 | `e7e39d0ea1f673659c44440c592ecdfa68d21f9ddf5b833a95e5f66bede1b6c5` |
| MD5 | `cdced17ec8ce14f4a6782bd30193b926` |
| magic / version | `GRPH` / `1` |
| 头部里存的 checksum | `0x2e583b0c0ea8113b` |

**文件名 = `links-` + 图的 128 位哈希十六进制 + `.db`。**
文件名里的 `3b11a80e0c3b582e808cf07fc8266ddc` 共 16 字节，**前 8 字节按小端解释就是文件里存的 int64**（`3b 11 a8 0e 0c 3b 58 2e` → `0x2e583b0c0ea8113b`）。
该哈希 = `DecimaHash.murmur3().hash(Bytes.wrap(linkTable))`，即对**该游戏版本的 streaming graph「链接表」**做 128 位 Murmur3（reversed-toolbox `HashFunction.murmur3(42)`），用于标识「这份库属于哪个游戏版本」。**没有游戏原始数据就无法重算此哈希**，因此也不能离线判断一份库是否过期。

库由 odradek 的 **Usages（引用/使用关系）面板**生成（`UsagesToolPanel.BuildDatabaseWorker`），文件放在 odradek 的配置目录（Windows：`%LOCALAPPDATA%\Odradek\`）。打开时若 magic / version / checksum 对不上，odradek 分别抛 `Invalid database file magic` / `Unsupported database file version` / `Link table checksum mismatch`，然后重建。

---

## 2. 它记录了什么（一句话）

> **整张游戏资源图的反向引用索引**：图上每个 object 各有一条记录，列出「**谁引用了它**」——
> 引用者的 `groupId`/组内下标，以及引用发生在引用者内部的**哪个字段路径**上。

它**不包含任何名字、类型名、字符串**，只有整数 id；要翻译成人类可读的东西，必须配合游戏本体的 streaming graph。

---

## 3. 二进制布局（全部小端）

```
偏移        类型           含义
0           int32          magic = 'G' | 'R'<<8 | 'P'<<16 | 'H'<<24，即字节 47 52 50 48
4           int32          version = 1
8           int64          checksum（图哈希的低 8 字节，小端）
16          int32[count]   offsets：每个 object 的记录在文件中的绝对偏移，count = 图上 object 数
16+4*count  16 字节        **保留填充，全 0**
32+4*count  ...            数据区
```

* 数据区第一条记录的偏移 = `offsets[0]` = `32 + 4*count`（实测 21,416,816）。
  **注意写入器是 `writer.position(32 + objects * 4)`**，所以表尾与数据区之间固定有 16 字节零填充——解析时必须用 `offsets[0]` 或这个公式，不能假设数据紧跟在表后面。
* `offsets` 严格递增（每个 object 至少占 1 字节），`offsets[i]` 是 object #i 的记录起点，终点 = `offsets[i+1]`（最后一个到文件末尾）。
* 偏移是 **int32**，写入端用 `Math.toIntExact` → 该文件格式上限 2 GiB。

### 数据区：每个 object 一条记录

```
varint  linkCount
重复 linkCount 次：
    varint  srcGroupId     引用者所在 group id
    varint  srcObjectIndex 引用者在**该 group 内**的下标
    varint  pathLen
    uint8[pathLen] path    引用发生在引用者内部的字段路径
```

* varint = 无符号 LEB128：每字节低 7 位有效、最高位为续位，先低后高；读入为 32 位。实测各字段最多用 3 字节。
* 外层数组下标 `i`（0 .. count-1）是**全局 object 序号**，等于 `graph.group(gid).typeStart() + 组内下标`；`graph.types()` 就是全图 object 类型表（长度 = count），组内 object 在全局序号里是连续的一段 `[typeStart, typeStart + typeCount)`。
* **记录里的 `srcGroupId` / `srcObjectIndex` 是「引用者」（source）**，不是被引用者；被引用者由外层下标 `i` 决定。
* 同一「引用者 → 被引用者」可以出现多条（通过不同字段引用），但**完全相同的 (源 group, 源下标, path) 三元组只出现一次**。

### path 的语义（关键）

`path` 是若干 varint 元素，每个元素：

| 元素值 | 含义 |
|---|---|
| `e` 为偶数 | 类属性，属性下标 = `e >> 1` |
| `e` 为奇数 | 容器元素，元素下标 = `e >> 1` |

* 起点是**引用者对象的类型**：`graph.group(srcGroupId).types().get(srcObjectIndex)`。
* 属性下标是 `ClassTypeInfo.orderedAttrs()` 的下标；对 DS2 而言 = **只含可序列化属性、按序列化偏移排序（含继承的基类属性，基类在前）**。
* 下标/属性都随路径逐级切换：遇到 Attr 就用当前类的属性表，遇到 Index 就用当前容器类型。
* 序列**必然以属性（偶数）开头**（实测 14,765,058 条全部如此，0 例外），因为根一定是 class。
* 等价的可读形式：`o.<attr>[<i>].<attr>...`，最后一步通常落在指针属性上。

排除规则（决定了库里「没有什么」）：

* 指针值为 null 的引用**不记录**（实测 `srcGroupId == 0` 的记录为 0 条）。
* 只记录指向图上 object 的指针（`ObjectIdHolder`）；枚举 / 原子 / bitset 之类不产生记录（`BitSetTypeInfo` 在访问器里直接 `NotImplementedException`）。
* 元素类型是原子的容器**整棵跳过**（`visitContainer` 的特化），所以数组里的基础类型值不会出现在 path 里。
* 不可序列化属性（属性型 / property）不会出现。

### 记录顺序

数据区按 `LinkDatabase.build()` 扫描顺序写入：外层遍历 `graph.groups()`（**不按 id 排序**），内层按组内 object 下标、按字段访问顺序追加。
所以**同一个 object 的引用列表按 group id 不是有序的**（实测 328,379 处逆序）。做 diff/比较时要么按原序、要么自行排序，别假设有序。

---

## 4. 实测统计（可直接当解析器的黄金结果）

| 指标 | 值 |
|---|---|
| count（图上 object 数） | **5,354,196** |
| 引用记录总数 | **14,765,058** |
| 有被引用的 object | 5,352,067；**0 引用的** 2,129 |
| 引用来源 | **36,503 个 group**，3,766,206 个 object |
| srcGroupId 范围 | 1 .. 79,317（**无 0**） |
| srcObjectIndex 最大值 | 198,233 |
| offsets 表区间 / 数据区 | `[16, 21,416,800)` / `[21,416,816, 147,420,326)` |
| 最后一个 offset | 147,420,319（末条记录 7 字节，正好接到 EOF） |
| 重复的 (源 group, 源下标) 对不同 path | 2,750,873 |
| 完全重复的 (源, path) 三元组 | 0 |

入度（被引用次数）分布，n → object 数：
`0→2,129; 1→4,608,054; 2→544,975; 3→42,486; 4→28,726; 5→11,481; 6→14,736; 7→8,309; 8→10,237; 9→7,173; 10→4,310; 11→5,119 …`
最大的几个：`320,685`（全局序号 **#60110**）、`316,385`（#44689）、`313,684`（#38922）、`116,503`（#55）、`100,209`（#31098）。

最活跃的引用来源 group（引用条数）：`499→432,837`、`579→395,522`、`31126→211,609`、`56→172,932`、`467→172,919`、`54152→154,319`、`530→143,669`、`44→140,559`、`829→117,533`、`41→115,092`、`552→80,951`、`967→76,825`、`723→75,300`、`30611→75,217`、`48047→70,497`。
（用本仓库的导出文件名交叉比对，这些 group 里混有 `GraphSoundResource` / `WwiseWemResource` / `WwiseBankResource` / `GraphProgramResource` / `NodeConstantsResource` / `WwiseID` 等类型——即 **Wwise event/bank/wem 之间的引用关系也在库内**。用 `python utils/odradek_links.py groups` 可复现。）

path 形态（A = 属性步，I = 容器元素步），共 **16 种**：

| 形态 | 条数 | | 形态 | 条数 |
|---|---|---|---|---|
| `A` | 5,425,172 | | `AAA` | 25,501 |
| `AI` | 3,341,152 | | `AIIAIA` | 16,163 |
| `AIAIAIA` | 3,048,598 | | `AIIAI` | 13,194 |
| `AIA` | 1,300,288 | | `AAIAAI` | 3,992 |
| `AIAIA` | 646,314 | | `AIAAA` | 3,258 |
| `AA` | 589,188 | | `AIAI` | 1,054 |
| `AAI` | 153,068 | | `AAIAA` | 1 |
| `AAIA` | 149,013 | | `AIAA` | 49,102 |

* 全部以 `A` 开头；结尾 11,252,598 条是 `A`（指针属性直达），3,512,460 条是 `I`（指针位于「结构体数组」元素内）。
* path 字节长度分布：1→5,413,494；2→2,764,035；3→1,775,999；4→877,986；5→861,533；6→23,179；7→2,856,089；8→192,743（最大 8 字节 / 7 个元素）。
* varint 字节数：srcGroupId 1/2/3 字节 = 786,872 / 10,882,659 / 3,095,527；srcObjectIndex = 2,932,480 / 10,154,232 / 1,678,346；pathLen **恒为 1 字节**。

### 全文件校验（`python utils/odradek_links.py verify` 全绿）

* magic == `GRPH`；version == 1
* `offsets[0] == 32 + 4*count`
* 16 字节填充全 0
* offsets 严格递增且都在文件内
* **14,765,058 条记录逐条走完后，每条都恰好终止于下一个 offset（0 处错位）**
* 每个 object 内部无重复 `(源 group, 源下标, path)` 三元组

---

## 5. 这份库给不了什么（易踩的坑）

1. **没有任何名字/类型名。** 只有整数 id 和字段下标。
2. **`typeStart` 表不在文件里。** odradek 查「谁引用了对象 `G:I`」是用 `typeStart[G] + I` 算全局序号的；反查（全局序号 → `G:I`）同样需要这张表。**没有游戏本体的 streaming graph，就无法把本文件里的全局序号翻译成 group:对象。**（本仓库里的 `GraphSoundRes/*.json` 等导出文件名只给了 `(group, index)`，也给不出 `typeStart`。）
3. **checksum 无法离线重算**（需要图的 linkTable），因此不能判断文件是否对应当前游戏版本——只能靠 odradek 自己校验。
4. **与游戏版本强绑定**：游戏一更新，`computeHash` 变化 → 文件名变化 → 旧库不会被复用（不会「错误命中」）。
5. 记录的是**入边**；出边需要读对象本身（odradek 的 `getOutgoingLinks` 是重新遍历对象，不查库）。
6. 容器/指针语义的细节依赖 odradek 的 RTTI 定义（属性顺序、哪些属性可序列化），换游戏（HFW/DS2）实现不同，属性下标含义可能不同。

---

## 6. 使用配方

**A. 已有游戏 graph 的 agent（能拿到 `graph.group(id).typeStart()/types()`）**

```text
# 入边
i = graph.group(target.groupId()).typeStart() + target.objectIndex()
for (srcGroup, srcIndex, path) in db.links_for(i):
    srcType = graph.group(srcGroup).types().get(srcIndex)     # 引用者的类型
    srcId   = ObjectId(srcGroup, srcIndex)                     # 引用者身份
    field   = render(srcType, path)                            # o.attr[3].attr...
```

`render`：从 `srcType` 出发，偶数 `e` → `type = type.orderedAttrs().get(e>>1)` 然后 `type = attr.type()`；奇数 `e` → `type = type.asContainer().itemType()`，输出 `.名字` / `[下标]`。

**B. 没有 graph 的 agent**

只能做结构层面的工作：统计、按全局序号查询入度与来源、导出 JSONL 供后续用游戏侧数据补齐：

```powershell
python utils\odradek_links.py stats                  # 头部 + 汇总
python utils\odradek_links.py verify                 # 全文件一致性
python utils\odradek_links.py groups --top 40        # 最活跃来源 group（+已知类型名）
python utils\odradek_links.py incoming 60110         # 某全局序号的入边明细
python utils\odradek_links.py export links.jsonl     # 全量导出（~15M 行，约 1 GB，慎用）
```

参考实现里 `LinkDatabase` 类可直接 `import`（`utils/odradek_links.py`），`count` 由 `offsets[0]` 反推，无需外部参数。

---

## 7. 能不能「按类型搜索对象」？（例如 WwiseWemResource）

**只用 db：不能。** db 里没有任何类型信息，目标只有全局序号，源只有 `组:下标`。

**db + 本仓库的导出文件：能，而且已验证。** 因为导出文件同时给出两样东西：

* 文件名 → 对象的 `组:下标` 和**类型名**（`WemResJson\WwiseWemResource_1004_1266.json`）；
* 内容里的对象指针 → `"字段名": "<ref to 组:下标>"`，即**已知的源→目标边**（带字段名）。

同一条边在 db 里也存在（目标是全局序号），于是 `typeStart[目标组] = 全局序号 - 目标下标`。工具 [utils/odradek_refs.py](utils/odradek_refs.py) 分三步解出这张表：

1. 只用「恰好 1 条出边」的源对象直接解 —— 每个组会收到成百上千张独立的票；
2. 迭代：源的出边里只剩一个组未解出时，用余项确定它；
3. 出边**全部落在同一个未解出的组**里时，用「整体多重集精确匹配」唯一确定平移量。

**实测结果（本机这份 db + 导出）：**

| 项 | 值 |
|---|---|
| 有出边的导出对象 | 45,225（全部能在 db 里找到，0 缺失） |
| 解出 typeStart 的组 | 3,259 个 |
| 交叉验证：出边多重集与 db **完全一致**的源对象 | **43,640 个，0 个不符** |
| `WwiseWemResource` 对象总数（导出） | 7,838（group 1604 最多，5,237 个） |
| 其中可查引用（所在组已解出） | **7,248 个**；剩余 590 个在 8788 / 9691 等「无导出对象指向」的组 |

**副产品：字段名可以还原。** 导出 JSON 的键顺序 == `orderedAttrs()` 顺序，实测完全吻合：
`GraphProgramResource.ExposedDataResource = 属性 7`（25,663/25,663 票）、
`GraphSoundResource.Group = 1`、`GraphSoundResource.GraphProgram = 36`（各 5,700/5,700 票）。
所以对这两种类型，path 里的 `aN` 可直接翻译成字段名（工具以 `field~名字` 显示）。

**命令：**

```powershell
python utils\odradek_refs.py index                  # 扫描导出目录（缓存到 utils/export_index.json）
python utils\odradek_refs.py bootstrap              # 解 typeStart → utils/type_start.json（含一致性校验）
python utils\odradek_refs.py list WwiseWemResource  # 列出该类型全部对象（组:下标）
python utils\odradek_refs.py refs 1604:2556         # 谁引用了它（含引用者类型 + 字段路径）
```

实际输出示例：

```text
WwiseWemResource  (1604:2556, global index 4247727) has 1 incoming reference(s)
  <- WwiseBankResource      1604:4681    o.a5[0]
```

**边界：** 未出现在导出里的对象仍然只有 id、没有类型名；`typeStart` 也只对「有导出对象指向的组」有效。要 100% 覆盖，必须从游戏本体 streaming graph 的 `StreamingGroupData.typeStart` 取值。

---

## 8. 复现本文档所有结论

```powershell
python utils\odradek_links.py stats
python utils\odradek_links.py verify
python utils\odradek_links.py groups --top 40
python utils\odradek_refs.py bootstrap
Get-FileHash "$env:LOCALAPPDATA\Odradek\links-*.db" -Algorithm SHA256
```
