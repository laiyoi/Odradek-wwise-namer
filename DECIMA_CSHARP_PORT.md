# Decima / DS2 资源读取与导出：odradek 源码考证 + C# 实现

> 全部结论来自对 **ShadelessFox/odradek 当前 master 源码**的逐行阅读，并在本机真实 DS2 安装
> （`K:\SteamLibrary\steamapps\common\DEATH STRANDING 2 - ON THE BEACH`）上**实测验证**。
> 凡未从源码确认的点，一律标 `[UNVERIFIED]`，不做 Decima 通用常识推测。
>
> 配套材料：
> * C# 实现：[OdradekSharp/](OdradekSharp/)（可编译、可运行，已通过实测）
> * 子报告：[streaming_graph_研究报告.md](streaming_graph_研究报告.md)、[utils/_odradek_src/ODRADEK_API_NOTES.md](utils/_odradek_src/ODRADEK_API_NOTES.md)
> * odradek 源码副本：`utils/_odradek_src/odradek-master/`（下文所有 `文件:行号` 相对该目录）

---

## 0. 一句话结论与实测证据

**一个 C# 实现可以完全复刻 odradek 的读取结果**：本仓库的 `OdradekSharp` 已能从真实 DS2 安装
加载 Streaming Graph、按类型搜索对象、按 ObjectId 读取对象、并输出与 odradek **逐字节相同**的 JSON。

实测（本机 DS2，2026-10 版本）：

| 验证项 | 结果 |
|---|---|
| 加载 `streaming_graph.core` | 成功；**5,354,196** 个 object、**79,323** 个 group、**241** 个 files、link table **30,229,145** 字节、**50,517** 个 root |
| 类型哈希算法 | `murmur3_x64_128(seed=42)("00000001_"+名称)` 的 **h1**，与文件头 8 字节 `0x929d7af6a30cd1c5` **完全吻合** |
| 按类型搜索 | `WwiseWemResource` **7,838**、`GraphSoundResource` **5,700**、`WwiseID` **7,000**、`WwiseBankResource` **74** —— 与 odradek 实际导出的文件数**一一相等** |
| JSON 导出 | 与 odradek 已导出的文件**逐字节相同**：`WemResJson/` 中 150/150 全等；专门构造的 4 个跨子组样本也全等 |
| 单文件锚点 | `1604:2556` → 304 字节 JSON，与 `WemResJson/WwiseWemResource_1604_2556.json` **完全相同** |

---

## 1. 关键源码文件与职责

根 = `odradek-master/`。`…/rtti/` = `odradek-rtti/src/main/java/sh/adelessfox/odradek/rtti/`。

### 1.1 入口与游戏层

| 文件 | 职责 |
|---|---|
| `odradek-game/src/main/java/sh/adelessfox/odradek/game/Game.java:39-48` | `Game.load(Path)`：ServiceLoader 找 `Provider`，要求 `supports()` 命中者**恰好 1 个** |
| `odradek-game-ds2/.../game/DS2Game.java:27-37` | DS2 判据 `Files.exists(path.resolve("DS2.exe"))`；构造 `fileSystem` → `DS2TypeFactory` → 读图 → `StreamingGraphStorage` → `StreamingGraphImpl` → `StreamingObjectReader` |
| 同上 `:136-144` | 读入口：`BinaryReader.open(fileSystem.resolve("cache:package/streaming_graph.core"))` + `new DS2TypeReader().readObject(...)` |
| 同上 `:146-156` | 设备路径解析：`cache:` → `{root}\LocalCache{platform}\…`，`source:` → `{root}\…`，`tools:` → `{root}\tools\…` |
| 同上 `:80-111` | `readDataSource*` / `readFile` → `storage.read(file, offset, length)` |
| `odradek-app/src/main/java/sh/adelessfox/odradek/app/Main.java:24-45` | CLI 入口（picocli），`-s/--source` 指定游戏根目录 |
| `odradek-app/.../cli/ExportAssetCommand.java` | `export <组:下标>… -f <格式> -o <目录>`；文件名 `"%s_%s_%s".formatted(type, groupId, objectIndex)` |

### 1.2 Streaming Graph

| 文件 | 职责 |
|---|---|
| `odradek-game-decima/.../game/decima/StreamingGraph.java` | 接口：`Group{id,typeStart,types,subGroups,rootIndices,spans,locators,links}`、`Span(fileIndex,offset,length)`、`Locator(fileIndex,offset)`、`Link(OptionalInt group,int index)` |
| `odradek-game-ds2/.../storage/StreamingGraphImpl.java:40-58` | 构造：`groups → groupIds → subGroups → superGroups → typeTable → linkTable → spans → locators → files` |
| 同上 `:149-186` | `readTypeTable`：`TypeTableData` = 5×int32 头（`compression=0, stride=2, count, count2, 1`）+ count×**uint16** 索引 → `TypeHashes[index]` → `factory.get(DS2TypeId.of(hash))` |
| 同上 `:188-194` | `readLinkTable`：`storage.read(files[LinkTableID], 0, LinkTableSize)` |
| 同上 `:110-120` | `Locator(fileIndex = data & 0xffffff, offset = data >>> 24)`；`Span(fileIndex = fileIndexAndIsPatch & 0x7fffffff, offset, length)` |
| 同上 `:196-220` | `readLink` / `readVarInt`：**大端（MSB-first）base-128** varint；首字节 bit6=0x40 表示带 group 字段 |
| 同上 `:222-280` | `GroupImpl`：`types = typeTable[typeStart, +typeCount)`、`spans/locators/subGroups` 同样切片、`links() = graph.links(linkStart)` |

### 1.3 存储与解压

| 文件 | 职责 |
|---|---|
| `odradek-game-ds2/.../storage/StreamingGraphStorage.java:47-96` | `mount`：先 `DirectStorageReader.open`，失败退回 `BinaryReader.open`；`read` = `position(offset)` + 读 length 字节 |
| `odradek-core/.../io/DirectStorageReader.java:50-93` | DSAR 头（32B）+ chunk 表（每项 32B）；`decompress` = **LZ4 block** |
| `odradek-core/.../io/ChunkedBinaryReader.java:71-94` | 按 `chunk.offset <= pos` 找块、解压整块、跨块循环拷贝 |
| `odradek-core/.../io/ChannelBinaryReader.java:13-15` | 16 KiB 缓冲，**小端** |

### 1.4 RTTI 与对象反序列化

| 文件 | 职责 |
|---|---|
| `…/rtti/generator/TypeContext.java:29-93` | `load()`：**先 extensions.types → 再 extends → 再 types.json（覆盖）→ 统一解析前向引用**；`process()` 按 kind 分派 |
| 同上 `:100-169` | compound：`version/flags/messages/bases/attrs`；扩展类型以 **offset = −1** 追加进 `bases` |
| 同上 `:126-155` | attr：`name/type/offset/flags` + 可选 `min/max/comment/property`；`{"category": …}` 是**占位分组标记**（影响 `group()`） |
| `…/rtti/ClassAttrInfo.java:5-29` | **`isSerialized() = (flags & 2) == 0`**（bit1 = ATTR_DONT_SERIALIZE_BINARY）；`isProperty` 来自 JSON 的 `property` 字段 |
| `…/rtti/factory/AbstractTypeFactory.java:73-90,128-139` | `collectOrderedAttrs`（**沿基类累加 offset**，跳过 offset<0）→ `sortOrderedAttributes` → `filterOrderedAttributes` |
| `…/rtti/ClassTypeInfo.java:42-54` | `serializedAttrs()`：基类递归在前 + 本类声明序（**不排序**；扩展基类也带进来）—— 这是 JSON 导出的顺序 |
| `odradek-game-ds2/.../rtti/DS2TypeFactory.java:19-45,47-89` | TypeId = `murmur3(seed 42)("00000001_"+name).asLong()`；**LCG pivot quicksort** + property 排最后 |
| `…/rtti/io/AbstractTypeReader.java:20-83` | 分派 + **按 `orderedAttrs()` 顺序串行读取**（不 seek、无对齐）；读完调用 `ExtraBinaryDataHolder.deserialize` |
| `odradek-game-ds2/.../rtti/DS2TypeReader.java:23-43` | 文件级对象头：`u64 typeHash \| i32 size \| body \| i32 numLinks(==0)`，并断言 body 字节数 == size |
| 同上 `:45-208` | enum（1/2/4 字节，**有符号**）、container（`i32 count` + 内联元素；HashMap/HashSet 每项前 skip 4）、atom 表（String 带 CRC-32C / WString / StringHash / uint128 / MotionMatchingVecN / 半精度…） |
| `odradek-game-ds2/.../storage/StreamingObjectReader.java:100-135` | `readSingleGroup`：按 `group.types()` 预建对象；对每个 span 读字节后 `while(remaining>0)` 顺序 `fillCompound` |
| 同上 `:146-239` | `readPointer`：**1 字节 presence**；`UUIDRef` 另读 16 字节；其余从组 link 游标取；`Ref/cptr/WeakPtr` 的 group 字段是**父组 subGroups 的下标**，`StreamingRef` 的是**全局组 id** |
| 同上 `:157-176` | `StreamingDataSource` 的 `Locator` 由组 locator 游标赋值：`(offset << 24) \| (fileIndex & 0xffffff)` |
| `odradek-game-ds2/.../rtti/callbacks/*.java` + `module-info.java:52-67` | 14 个 DS2 回调，读 `MsgReadBinary` 额外数据 |

### 1.5 搜索 / 转换 / 导出

| 文件 | 职责 |
|---|---|
| `odradek-game-decima/.../util/GraphWalker.java` | 静态工具：按 group 元数据预过滤 → `readGroup` → 逐对象 `isInstance`。**本仓库零调用点（死代码）**；其做法也不是最优 |
| `odradek-game-decima/.../ObjectIdHolder.java:8-11` | **零反序列化的类型查询**：`graph.group(id.groupId()).types().get(id.objectIndex())` —— 这才是"按类型搜索"的正确姿势 |
| `odradek-game/.../Converter.java:20-97` | `convert(object, game)`；`converter(info, R)`：目标可赋值 → noop；否则按输入/输出类型过滤，**0 个返回 empty、≥2 个抛异常** |
| `odradek-game/.../Exporter.java:17-143` | `id()/name()/supportedType()`；`OfSingleOutput`（有 `extension()`）/`OfMultipleOutputs` |
| `odradek-export-json/.../json/JsonExporter.java:23-131` | JSON 导出：裸对象、`serializedAttrs()` 顺序、null 值跳过、`byte[]`→Base64、指针→`String.valueOf(obj)` |
| `odradek-game-ds2/.../rtti/data/ref/Ref.java:41-44` + `ObjectId.java:24-27` | `<ref to 组:下标>` 的确切来源 |

---

## 2. 调用链（读取一个对象）

```
DS2Game.readObject(ObjectId)
  → readGroup(groupId, readSubgroups = true)                    [DecimaGame.java:18-20]
    → StreamingObjectReader.readGroup(id, new HashMap<>(), true) [StreamingObjectReader.java:55-75]
      → readGroup(Group, true)                                   [:77-98]
          ① 对每个 subgroup 递归 readGroup(sub, true) → currentSubGroups
          ② resolveStreamingLinksAndLocators = readSubgroups
      → readSingleGroup(Group)                                   [:100-135]
          objects[i] = newInstance(group.types()[i])
          for span in group.spans():
              bytes = storage.read(files[span.fileIndex], span.offset, span.length)
              while reader.remaining() > 0:
                  fillCompound(objects[index].type, reader, factory, objects[index]); index++
                  ↑ 类型来自 type table，对象字节流里没有任何对象头/长度/对齐
    → index 取出目标对象
```

对象字节流的读取顺序 = `ClassTypeInfo.orderedAttrs()`：
`[基类递归（累加 offset，跳过 offset<0 的扩展基类） + 本类 attrs] → 非 property 按绝对 offset 升序 → property 按名字升序 → 去掉 (flags & 2) != 0`。

---

## 3. 文件格式（实测确认）

### 3.1 `streaming_graph.core`（= `{root}\LocalCacheWinGame\package\streaming_graph.core`）

**不是 DSAR**，是裸的 Decima 资源对象：

```
u64  typeHash   = murmur3_x64_128(seed 42)("00000001_StreamingGraphResource").h1  -- 实测 0x929d7af6a30cd1c5
i32  size       = 后续 body 的字节数（实测断言通过）
body            = StreamingGraphResource 的 orderedAttrs 顺序序列化
i32  numLinks   = 0（必须）
```

资源字段（按二进制顺序）：`ObjectUUID(GGUUID,16B)`、`IsPacked(bool)`、`TypeHashes(Array_uint64)`、
`TypeTableData(Array_uint8)`、`LinkTableID(u64)`、`LinkTableSize(i32)`、`LocatorTable`、`ArrayTable`、
`SpanTable`、`Groups`、`SubGroups`、`RootUUIDs`、`RootIndices`、`Files(Array_Filename)`、
`PackFile*`、`ObjectLocators`、`UUIDLinkTable`。
实际被 odradek 使用的只有：TypeHashes / TypeTableData / LinkTableID / LinkTableSize / LocatorTable /
SpanTable / Groups / SubGroups / RootIndices / Files。

* **group 数据**（`StreamingGroupData`）：`GroupID@0, NumObjects@4, GroupSize(i64)@8, SubGroupStart@16, SubGroupCount@20, RootStart@24, RootCount@28, SpanStart@32, SpanCount@36, TypeStart@40, TypeCount@44, LinkStart@48, LinkSize@52, LocatorStart@56, LocatorCount@60, ObjectUUIDs@64`（`NumObjects/GroupSize/LinkSize/ObjectUUIDs` 未被使用）
* **type table**：`TypeTableData` = `i32 compression(0) | i32 stride(2) | i32 count | i32 count2 | i32 1` + `count × u16 索引`；`TypeHashes[index]` → 类型哈希 → 类型名
* **span**：`i32 fileIndexAndIsPatch | i32 length | i32 offset`（**length 在 offset 之前**，须按字段名读）
* **locator**：`u64 data` → `fileIndex = data & 0xFFFFFF`，`offset = data >>> 24`
* **link table**：`Files[LinkTableID]` 的 offset 0 起 `LinkTableSize` 字节（DS2 实测就是 `streaming_links.stream`，30,229,145 字节）；每条 link：首字节 bit6=0x40 → 先读 group varint（初值 `first & 0xbf`）再读 index varint，否则 index = varint(`first & 0xbf`)；varint 为**大端拼接** `value = (value << 7) | (b & 0x7f)`，续位 0x80

### 3.2 DSAR 归档（`package.*.core` / `*.core.stream`）

```
0x00 u32 magic "DSAR"   0x04 u16 versionMajor(3)   0x06 u16 versionMinor(1)
0x08 u32 chunkCount     0x0C u32 firstChunkOffset(= 32 + chunkCount*32)   0x10 u64 totalSize(解压后)
0x18 8 字节 "PADDING*"
chunk[i] @ 32+32*i:  i64 offset(解压后逻辑偏移, 首项=0, 连续) | i64 compressedOffset(物理偏移, 不连续)
                    i32 size(解压后) | i32 compressedSize | u8 type(=3 → LZ4 block) | 7 字节 0x55
```

`Read(offset, size)`：二分找到 `chunk.offset <= offset` 的块 → 读 `compressedSize` 字节 → **裸 LZ4 block 解压**成恰好 `size` 字节 → 拷贝；跨块循环。

### 3.3 RTTI（`types.json` + `extensions.json`）

* 顶层是 **对象**：类型名 → 定义。`kind ∈ {compound, atom, container, pointer, enum, enum flags, enum bitset}`
  * `compound`：`version/flags/bases[{type,offset}]/attrs/messages`
  * `atom`：`base_type`（一层别名，如 `Filename → String`）
  * `container`：`type`（Array/HashMap/HashSet/定长宏名）+ `item_type`
  * `pointer`：`type`（**指针种类** Ref/UUIDRef/cptr/StreamingRef/WeakPtr）+ `item_type`（**目标类型**）
  * `enum`/`enum flags`：`size`(1/2/4) + `values[{name,value,alias?}]`；`enum bitset`：`size` + `type`（odradek 未实现读取）
* attr：`name/type/offset/flags` + 可选 `min/max/comment/property`；**`{"category":"X"}` 是分组占位条目**
* `isSerialized = (flags & 2) == 0`；`isProperty` 是独立布尔（全库 252 个，offset 一律 0，排在最后读）
* `extensions.json`：`extends` 给 12 个类型追加 **offset = −1 的扩展基类**；`types` 新增 24 个合成类型（回调的字段载体）

### 3.4 各类型的字节规则

| 类型 | 规则 |
|---|---|
| `bool` | 1 字节，**只能 0/1** |
| `int8/uint8` `int16/uint16` `int/uint/int32/uint32/ucs4` `int64/uint64/uintptr` | 1/2/4/8 字节，**全部按有符号读** |
| `tchar/wchar` | 2 字节（short → char） |
| `HalfFloat` | 2 字节半精度 |
| `float/double` | 4/8 字节 |
| `uint128` | 16 字节（小端 → 大整数） |
| `String` | `i32 字节长度`；为 0 → 空串；否则 `i32 CRC-32C 校验` + 长度字节 UTF-8，**校验不符即报错** |
| `WString` | `i32 字符数` + 字符数×2 字节 UTF-16LE（无校验） |
| `StringHash` | `i32 size(必须 4)` + `i32 值` |
| `MotionMatchingVecN` | 72 个 float（288 字节） |
| enum | `size` ∈ {1,2,4} 字节，**有符号**；`enum flags` 为位集语义 |
| container | `i32 count` + 紧邻元素（无对齐）；`HashMap/HashSet` 每项前 4 字节 hash |
| class | 按 orderedAttrs 顺序串行，无对齐/无 padding |
| pointer | **1 字节 presence**；0 → null（不消耗 link）；1 且 UUIDRef → 16 字节 GGUUID；否则从组 link 游标取一个（不消耗字节） |
| `MsgReadBinary` | 属性读完后调用回调（**派生类继承基类回调**） |

### 3.5 14 个 DS2 回调（必须字节精确）

`DataBufferResource`（`i32 count`；=0 结束；否则 `i32 isStreaming/i32 flags/i32 format/i32 stride` + 非流式 `stride*count` 字节）、
`DebugMouseCursorPS4`、`IndexArrayResource`（`count/flags/format/streaming` + `MurmurHashValue(16)` + 非流式 `count * stride`，**Index16=0→2B、Index32=1→4B**）、
`LocalizedTextResource`（对每个 writtenLanguage：`u16 串` + `u16 串` + `u8 mode`）、
`ShaderResource`（`i32 size` + `GGUUID` + `GGUUID` + `StreamingDataSource(9B)`）、
`Texture`（16 字节头 + `MurmurHashValue(16)` + `totalSize/embeddedSize/streamedSize/streamedMips` + **`totalSize − 12`** 字节）、
`TextureList`、`UITexture`、`UITextureFrames`、`VertexArrayResource`、`ZivaRTResource`、
以及 **未移植**的 `FacialRigSettingWithLODResource`（RigLogic）、`PhysicsRagdollResource` / `PhysicsShapeResource`（Jolt）。

---

## 4. C# 数据结构 ↔ Java 对应

| C#（本仓库） | odradek Java | 说明 |
|---|---|---|
| `Rtti.TypeFactory` | `TypeContext` + `AbstractTypeFactory` | 懒解析 types.json + extensions.json，含 `OrderedAttrs`（LCG quicksort + 累加 offset）与 `SerializedAttrs` |
| `Rtti.TypeInfo` / `ClassTypeInfo` / `AtomTypeInfo` / `ContainerTypeInfo` / `PointerTypeInfo` / `EnumTypeInfo` / `BitSetTypeInfo` | 同名接口 | 字段一一对应 |
| `Rtti.ClassAttrInfo` | `ClassAttrInfo` | `IsSerialized => (Flags & 2) == 0`；`IsProperty` 来自 JSON |
| `Rtti.TypedObject` | `TypedObject` / `DS2.*` 生成类 | 动态 `Dictionary<string, object?>`，无需代码生成 |
| `Rtti.RttiReader` | `AbstractTypeReader` + `DS2TypeReader` | 顺序反序列化、文件级对象头、回调分派 |
| `Rtti.TypeCallbacks` | `DS2` 的 14 个 `ExtraBinaryDataCallback` | 含**基类回调继承** |
| `Rtti.ObjectId` / `ObjectRef` / `UuidRef` / `EnumValue` | `ObjectId` / `Ref`·`cptr`·`WeakPtr`·`StreamingRef` / `UUIDRef` / `Value` | |
| `Ds2.StreamingGraph` | `StreamingGraphImpl` | groups / typeTable / spans / locators / files / linkTable |
| `Ds2.StreamingObjectReader` | `StreamingObjectReader` | 组读取、link/locator 游标、指针解析 |
| `Ds2.DecimaGame` | `DS2Game` | 打开游戏、类型搜索、读对象、读 StreamingDataSource |
| `Io.DataFile` / `DirectStorageFile` / `Lz4` | `DirectStorageReader` / `ChunkedBinaryReader` / `Decompressor.lz4Block()` | DSAR + LZ4 随机读 |
| `Export.JsonExporter` | `JsonExporter` + `SimpleTypeVisitor` + `GGUUIDTypeAdapter` | 逐字节复刻 |
| `Io.Hashes` | `DecimaHash` | `TypeHash` = murmur3_x64_128(seed 42) h1；`Crc32C` |

---

## 5. 最小可运行实现（已实测）

```
OdradekSharp/
├── OdradekSharp.csproj          # net10.0 控制台
├── Data/types.json              # 从 odradek-game-ds2/src/main/resources 复制
├── Data/extensions.json
├── Io/BinaryReader.cs           # 小端读取 + Murmur3 x64_128 + CRC-32C
├── Io/DataFile.cs               # DSAR 容器 + 随机读
├── Io/Lz4.cs                    # LZ4 block 解压
├── Rtti/TypeFactory.cs          # types.json/extensions.json → 类型表
├── Rtti/TypedObject.cs          # 动态对象、ObjectId、EnumValue、引用类型
├── Rtti/RttiReader.cs           # 顺序反序列化
├── Rtti/TypeCallbacks.cs        # 14 个 DS2 回调（11 个已实现）
├── Ds2/StreamingGraph.cs        # 图 + link 游标
├── Ds2/StreamingObjectReader.cs # 组读取 / 指针解析
├── Ds2/DecimaGame.cs            # 门面
├── Export/JsonExporter.cs       # odradek 兼容 JSON
└── Program.cs                   # CLI
```

命令：

```powershell
dotnet run -- info   "<gameDir>"                        # 图统计（groups/types/spans/link table）
dotnet run -- types  "<gameDir>" --filter Wwise          # 图上出现过的类型名 + 数量
dotnet run -- find   "<gameDir>" WwiseWemResource --limit 20
dotnet run -- read   "<gameDir>" 1604:2556 --json --out out.json
dotnet run -- dump   "<gameDir>" WwiseWemResource --out outdir --limit 500 --exact
dotnet run -- group  "<gameDir>" 5206                    # 组内对象类型清单（仅元数据）
dotnet run -- trace  "<gameDir>" 5206 --no-subgroups     # 逐对象字节区间（调试定位）
dotnet run -- hex    "<gameDir>" "cache:package/package.00.00.core" <offset> <len>
```

`info` 实测输出：

```
types in graph : 5354196      groups: 79323      files: 241
link table     : 30229145 bytes               root objects: 50517
```

---

## 6. 实现路线（对应用户给出的 Phase 1-7）

| Phase | 状态 | 说明 |
|---|---|---|
| 1 Streaming Graph | ✅ 已完成 | `open` → groups/spans/locators/files/typeTable/linkTable 全部可读 |
| 2 ObjectId | ✅ | `group.types()` 切片即得；`find` 输出 `组:下标` |
| 3 读单个对象 | ✅ | `read <组:下标>`；含 DSAR+LZ4 随机读 |
| 4 RTTI | ✅ | types.json/extensions.json 全量加载（20,130 个类型零错误），字段可按名访问 |
| 5 类型搜索 | ✅ | 纯元数据，零反序列化（`group.types()` + `isAssignableFrom`），实测计数与 odradek 导出一致 |
| 6 Dump JSON | ✅ | 与 odradek 逐字节相同 |
| 7 Converter / Exporter | 部分 | JSON 已实现；**贴图/音频/模型转换器未移植**（见下） |

**后续要做的（按价值排序）**

1. **补 3 个回调**：`PhysicsShapeResource` / `PhysicsRagdollResource`（Jolt 二进制）、`FacialRigSettingWithLODResource`（RigLogic）。目前实现用"子组降级跳过"绕过，能读目标对象，但会丢这些子组内的指针。
2. **Converter**：`Texture`/`TextureSet` → DDS/PNG（含 BCn 解压、tile 重排）、`WwiseWemResource` → WEM/WAV（RIFF 解析 + vgmstream 转码）、`Scene/Animation` → CAST。odradek 对应文件在 `odradek-game-ds2/.../converters/**` 与 `odradek-export-*/`。
3. **定长容器宏**（`uint32_4`、`Vec4_3`、`float_*_COUNT`）与 `Texture.StreamingMipOffsets` 的真实布局 —— 见第 7 节 `[UNVERIFIED]`。
4. 可选：把 `RttiReader` 改成"惰性读取"以加速大范围 dump（目前是整组读）。

---

## 7. 实际踩到的坑（都已修正，供后来者参考）

1. **属性排序必须用"累加后的绝对偏移"**，不是 attr 自身的 offset。`CurveResource` 的基类 `CurveData` 在 offset 32，用原始 offset 排序会导致 `Array_CurvePoint` 读到垃圾（"Negative container count"）。
2. **Property 属性**（`property: true`，252 个）必须排到最后（按名字升序）但仍要读取；`isProperty` 不是 flags 位。
3. **`isSerialized` 是 `(flags & 2) == 0`**（bit1 置位 = 不序列化），不是"bit 置位才序列化"。
4. **回调会被派生类继承**：`ShaderFromFileResource : ShaderResource` 需要读 `ShaderResourceCallback` 的 45 字节（对象从 25 字节变成 70 字节）。判定方式是 `target instanceof ExtraBinaryDataHolder`，等价于"沿基类链找声明 `MsgReadBinary` 的类型"。
5. **`EIndexFormat`：Index16 = 0、Index32 = 1**（不是 1/2）。
6. **pointer 在对象流里只有 1 字节**；link 表是独立文件，且 varint 是**大端拼接**（不是 LEB128）。
7. **`StreamingRef` 与 `Ref/cptr/WeakPtr` 的 group 字段语义不同**（全局组 id vs 父组 subGroups 下标）。
8. **`StreamingDataSource.Locator` 不在对象字节里**，来自组 locator 游标，打包公式 `(offset << 24) | (fileIndex & 0xffffff)`。
9. **`span` 结构体字段顺序是 `fileIndexAndIsPatch, length, offset`** —— 按字段名读，别按直觉。
10. **DSAR 的 `compressedOffset` 不连续**，必须用表里的值定位；解压是裸 LZ4 block。
11. **类型哈希 = murmur3_x64_128（不是 x86_32）取 h1**，已用真实文件头验证。
12. **JSON 顺序是 `serializedAttrs()`（声明序）**，不是读取顺序；`null` 属性整条跳过；`byte[]` → Base64；`GGUUID` 需要按 `data3..data0` 反序渲染成 GUID 字符串。

---

## 8. 未完成 / `[UNVERIFIED]`

* `[UNVERIFIED]` **定长容器宏**（`uint32_4`、`uint64_2`、`Vec4_3`、`float_GLOBAL_RENDER_VAR_COUNT` 等 16 个）真实布局：odradek 一律按 `i32 count + N 项` 读，若其实际无 count 则会错位。已知 `Texture.StreamingMipOffsets` 用此类。
* `[UNVERIFIED]` **排序键撞车**：10 个真实类存在"已序列化、非 property 的 attr 共享同一 offset"（`TelemetryDSVrTraining` 有 11 个同在 144）。C# 已逐行复刻 LCG quicksort 以保持一致，但这些类在 odradek 中是否真能正确解码未知。
* `[UNVERIFIED]` **attr flags 除 bit1 外的位**（0/2/4/5/8/9/13…）含义无源码定义。
* `[UNVERIFIED]` **compound 的 `flags`/`version`** 只用于 UI/Javadoc，不参与读写。
* `[UNVERIFIED]` **`enum bitset`**（`Platforms`、`ProgramSet`）：odradek 自身抛 `NotImplementedException`；本实现用 `--lenient` 跳过 1 字节继续（**与 odradek 不一致**，默认关闭）。
* `[UNVERIFIED]` **10 个未注册回调的 `MsgReadBinary` 类型**（`Pose`、`StaticTile`、`WorldMapSuperTile`、`Morpheme*`、`LayerData`…）：DS2 数据里若出现，odradek 会抛异常。
* 未移植：3 个 Jolt/RigLogic 回调、贴图/音频/模型 Converter（Phase 7 的实质部分）。
