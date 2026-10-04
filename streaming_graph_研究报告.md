# Odradek DS2 StreamingGraph 源码考证报告（面向 C# 移植）

源码根：`E:\Odradek-wwise-namer\utils\_odradek_src\odradek-master`
下文所有路径均相对该根目录；行号以当次读取为准。

---

## 0. 先决事实：`DS2.*` 是构建期代码生成的

`odradek-game-ds2/src/main/java/module-info.java:12-17` 在 module-info 上挂注解：

```java
// odradek-game-ds2\src\main\java\module-info.java:12-17
@TypeBindings(
    input = @TypeBindings.Input(
        types = "types.json",
        extensions = "extensions.json"
    ),
    target = "sh.adelessfox.odradek.game.ds2.rtti.DS2",
```

注解处理器 `GenerateBindingsProcessor` 在编译期读取 `types.json`，生成一个 Java 源文件（包 `sh.adelessfox.odradek.game.ds2.rtti`，接口 `DS2`）：

```java
// odradek-rtti-generator\src\main\java\sh\adelessfox\odradek\rtti\generator\source\GenerateBindingsProcessor.java:34-45
var context = new TypeContext();
try {
    context.load(openResource(annotation.input().types()), openResource(annotation.input().extensions()));
...
var targetDesc = ClassDesc.of(annotation.target());
...
var generator = new TypeSourceGenerator(targetPackage, targetClass);
```

每个 `kind == "compound"` 的类型生成一个 **嵌套 `static` 接口**，每个属性生成 `getter`/`setter` 抽象方法：

```java
// odradek-rtti-generator\src\main\java\sh\adelessfox\odradek\rtti\generator\source\TypeSourceGenerator.java:56-60
private TypeSpec buildClass(ClassTypeInfo info) {
    var builder = TypeSpec.interfaceBuilder(toTypeName(info))
        .addJavadoc("flags = $L, version = $L", info.flags(), info.version())
        .addModifiers(Modifier.PUBLIC, Modifier.STATIC)
        .addSuperinterface(TypedObject.class);
```

```java
// odradek-rtti-generator\src\main\java\sh\adelessfox\odradek\rtti\generator\source\TypeSourceGenerator.java:117-120
// Direct attribute accessors
for (ClassAttrInfo attr : attrs.getOrDefault(Optional.<String>empty(), List.of())) {
    builder.addMethods(buildAttr(attr));
}
```

**结论**：`DS2`、`DS2.StreamingGraphResource`、`DS2.StreamingGroupData`、`DS2.StreamingSourceSpan`、`DS2.StreamingDataSourceLocator`、`DS2.StreamingObjectLocator` 都是**编译期由 `odradek-game-ds2/src/main/resources/types.json` 生成的接口**（仓库里没有 `DS2.java`，只有 `types.json`）。

名称变换规则（决定 C# 里的属性名）：
- **类型名**：不转换，原样作为嵌套类名 —— `toTypeName(TypeInfo)` → `nestedClass(info.name())`
  （`TypeSourceGenerator.java:214-216`）。
- **属性名**：`normalizeCase`（`TypeGenerator.java:85-110`）→ 首字母小写；`GroupID`→`groupID`、`RootStart`→`rootStart`、`FileIndexAndIsPatch`→`fileIndexAndIsPatch`、`LinkTableID`→`linkTableID`。
- `m` 前缀且第二个字母大写时去掉 `m`；含 `_` 时按 `_` 转驼峰。

另外还有**第二套生成器**：`TypeRuntimeGenerator` 在**运行时**用 `MethodHandles` 动态生成字节码类 `DS2$<Type>$POD`（字节码实现的实体、字段、getter/setter），由 `AbstractTypeFactory` 在运行时驱动：

```java
// odradek-rtti\src\main\java\sh\adelessfox\odradek\rtti\factory\AbstractTypeFactory.java:33-35
generator = new TypeRuntimeGenerator(lookup, namespace.getPackageName(), namespace.getSimpleName());
generator.addBuiltins(getBuiltins());
context = new FactoryTypeContext();
```

```java
// odradek-rtti\src\main\java\sh\adelessfox\odradek\rtti\generator\TypeRuntimeGenerator.java:424-426
private ClassDesc toImplClassDesc(ClassTypeInfo info) {
    return toClassDesc(info).nested("POD");
}
```

C# 移植时这套运行时字节码生成**完全不需要**：C# 用普通类/结构体即可，只需要保留「属性顺序 = 按 offset 升序、且过滤 flags bit1」这一规则（见第 5 节）。

---

## 1. 游戏如何从目录被识别

### 1.1 入口：`Game.load(Path)`

```java
// odradek-game\src\main\java\sh\adelessfox\odradek\game\Game.java:39-48
static Game load(Path path) throws IOException {
    var providers = providers().filter(provider -> provider.supports(path)).toList();
    return switch (providers.size()) {
        case 1 -> providers.getFirst().load(path);
        case 0 -> throw new IllegalArgumentException("No provider found for " + path);
        default -> throw new IllegalStateException("Multiple providers found for " + path + ": " + providers);
    };
}
```

`providers()` 是 `ServiceLoader`（`Game.java:30-37`）。DS2 的 provider 在 `module-info.java:112-113` 注册：

```java
// odradek-game-ds2\src\main\java\module-info.java:112-113
provides sh.adelessfox.odradek.game.Game.Provider with
    sh.adelessfox.odradek.game.ds2.game.DS2Game.Provider;
```

### 1.2 DS2 vs HFW 的区分：只看根目录下的一个 exe 文件名

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\game\DS2Game.java:27-37
public static final class Provider implements Game.Provider {
    @Override
    public boolean supports(Path path) {
        return Files.exists(path.resolve("DS2.exe"));
    }

    @Override
    public Game load(Path path) throws IOException {
        return new DS2Game(path, DS2.EPlatform.WinGame);
    }
}
```

```java
// odradek-game-hfw\src\main\java\sh\adelessfox\odradek\game\hfw\game\HFWGame.java:27-37
public static final class Provider implements Game.Provider {
    @Override
    public boolean supports(Path path) {
        return Files.exists(path.resolve("HorizonForbiddenWest.exe"));
    }

    @Override
    public Game load(Path path) throws IOException {
        return new HFWGame(path, HFW.EPlatform.WinGame);
    }
}
```

要点：
- 判定是**纯文件名存在性判断**，`path` 必须是游戏根目录（exe 所在目录）。
- 两个 provider 都匹配时会抛 `IllegalStateException`（`Game.java:46`）。
- 平台固定写死 `WinGame`（`DS2Game.java:35`）。

`EPlatform` 枚举（`types.json:211371-211380`）：`size=4`，`0=PC, 1=PS4, 2=PS5, 3=Linux, 4=WinGame`；生成的枚举 `toString()` 返回枚举名（`TypeSourceGenerator.java:178-183`）。

### 1.3 游戏目录相对路径

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\game\DS2Game.java:140
try (var reader = BinaryReader.open(fileSystem.resolve("cache:package/streaming_graph.core"))) {
```

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\game\DS2Game.java:146-156
private record FileSystem(Path source, DS2.EPlatform platform) {
    public Path resolve(String path) {
        String[] parts = path.split(":", 2);
        return switch (parts[0]) {
            case "source" -> source.resolve(parts[1]);
            case "cache" -> resolve("source:LocalCache" + platform).resolve(parts[1]);
            case "tools" -> resolve("source:tools").resolve(parts[1]);
            default -> throw new IllegalArgumentException("Unknown device path: " + path);
        };
    }
}
```

因此 **`cache:package/streaming_graph.core` = `<游戏根>\LocalCacheWinGame\package\streaming_graph.core`**。
（`"LocalCache" + EPlatform.WinGame` → `LocalCacheWinGame`。）

设备前缀只有 3 种（`DecimaGame.java:50-64` 的 javadoc 亦有说明）：

| 设备 | 展开 |
|---|---|
| `source` | `<游戏根>\<path>` |
| `cache` | `source:LocalCache<Platform>`，即 `<游戏根>\LocalCacheWinGame` |
| `tools` | `<游戏根>\tools` |

`resolvePath` 在 `DS2Game` 里最终就是 `FileSystem.resolve`：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\game\DS2Game.java:113-116
@Override
public Path resolvePath(String path) {
    return fileSystem.resolve(path);
}
```

---

## 2. StreamingGraph 数据的来源

### 2.1 精确路径与探测代码

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\game\DS2Game.java:136-144
private static DS2.StreamingGraphResource readStreamingGraph(
    FileSystem fileSystem,
    TypeFactory typeFactory
) throws IOException {
    try (var reader = BinaryReader.open(fileSystem.resolve("cache:package/streaming_graph.core"))) {
        var result = new DS2TypeReader().readObject(reader, typeFactory);
        return (DS2.StreamingGraphResource) result;
    }
}
```

- **文件名**：`streaming_graph.core`
- **相对游戏根路径**：`LocalCacheWinGame\package\streaming_graph.core`（Windows；平台段由 `EPlatform` 决定）
- **没有做存在性/大小判断**：`BinaryReader.open` 直接打开，文件不存在就 `IOException`。唯一与「大小」有关的是资源自述 size 的一致性校验（见 2.3）。
- **reader**：`BinaryReader.open(Path)` → `ChannelBinaryReader.open`，**默认小端**（`ChannelBinaryReader.java:13-15` 用 `ByteOrder.LITTLE_ENDIAN`；`BinaryReader.java:14-20` 的 javadoc 明示 “By default, underlying data is interpreted as little endian”）。

### 2.2 是 Decima 资源，带资源头

`readObject` 就是资源头解析器：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\rtti\DS2TypeReader.java:23-43
public TypedObject readObject(BinaryReader reader, TypeFactory factory) throws IOException {
    var hash = reader.readLong();
    var size = reader.readInt();
    var type = factory.get(DS2TypeId.of(hash)).asClass();

    var start = reader.position();
    var object = readCompound(type, reader, factory);
    var end = reader.position();

    if (end - start != size) {
        throw new IllegalStateException(
            "Size mismatch for %s: %d (actual) != %d (expected)".formatted(type.name(), end - start, size));
    }

    int numLinks = reader.readInt();
    if (numLinks != 0) {
        throw new IllegalStateException("Expected 0 links, got " + numLinks);
    }

    return object;
}
```

**`streaming_graph.core` 文件布局（小端）**：

| 偏移 | 类型 | 含义 |
|---|---|---|
| 0 | `uint64` | 类型哈希（= `DS2TypeId.hash`，用于查 `TypeFactory`） |
| 8 | `int32` | 对象体字节数 `size` |
| 12 | … | `StreamingGraphResource` 的属性流（顺序见 2.4） |
| 12+size | `int32` | `numLinks`，必须为 0 |

`readObject` 在整个 DS2 模块中**只被调用一次**（grep 结果：`DS2Game.java:141` 唯一调用点），所以「资源头」格式只在这里出现。

### 2.3 Storage 侧读文件

`streaming_graph.core` 本身由 `BinaryReader.open` 直读（不走 storage）。**其余**文件（span 数据、link table）走 `StreamingGraphStorage`：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphStorage.java:47-80（压缩）
public void mount(String file) throws IOException {
    ...                                        // 已挂载则 warn 后直接返回
    Path path = game.resolvePath(file);        // ← 见 DS2Game.FileSystem.resolve
    if (Files.notExists(path)) { log.warn("File not found: {}", file); return; }

    BinaryReader reader;
    try { reader = DirectStorageReader.open(path); }   // 先按 DirectStorage(LZ4 分块) 解析
    catch (IOException e) { reader = BinaryReader.open(path); }   // 失败则退回裸文件
    ...
    files.put(file, reader);
}
```

- 先按 **DirectStorage 归档**（LZ4 分块，`DirectStorageReader.java:10-14, 41-43`）尝试解析，失败则退回**裸文件**。
- 读区间：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphStorage.java:82-96
public byte[] read(String file, long offset, long length) throws IOException {
    var reader = resolve(file);
    var buffer = new byte[Math.toIntExact(length)];

    if (length == 0) {
        return buffer;
    }
    ...
}
```

（**相对路径名 → 实际路径** 的解析全部经由 `DS2Game.resolvePath`，见第 6 节。）

### 2.4 `StreamingGraphResource` 属性流的读取顺序

odradek **不使用** types.json 里的绝对 `offset` 作为文件位置，只用它决定**读取顺序**（这是移植的关键）：

```java
// odradek-rtti\src\main\java\sh\adelessfox\odradek\rtti\factory\AbstractTypeFactory.java:73-84
private static void collectOrderedAttrs(ClassTypeInfo info, int offset, List<OrderedAttr> attrs) {
    for (ClassBaseInfo base : info.bases()) {
        if (base.offset() < 0) continue; // Extension type, see TypeContext#processCompound
        collectOrderedAttrs(base.type(), offset + base.offset(), attrs);
    }
    for (ClassAttrInfo attr : info.attrs()) {
        attrs.add(new OrderedAttr(info, attr, offset + attr.offset()));
    }
}
```

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\rtti\DS2TypeFactory.java:26-45
sortOrderedAttributes:  property 属性一律排到最后（同类内按名字排序）；
                       非 property 按 Integer.compare(offset) 升序。
filterOrderedAttributes: attrs.removeIf(attr -> !attr.attr().isSerialized());
```

```java
// odradek-rtti\src\main\java\sh\adelessfox\odradek\rtti\ClassAttrInfo.java:6-8, 27-29
// ERTTIAttrFlags
int ATTR_DONT_SERIALIZE_BINARY = 2;
...
default boolean isSerialized() {
    return (flags() & ATTR_DONT_SERIALIZE_BINARY) == 0;
}
```

```java
// odradek-rtti\src\main\java\sh\adelessfox\odradek\rtti\io\AbstractTypeReader.java:41-53
protected void fillCompound(
    ClassTypeInfo info,
    BinaryReader reader,
    TypeFactory factory,
    Object target
) throws IOException {
    for (AttributeReader attribute : getOrCreateDecodingPlan(info)) {
        attribute.read(reader, factory, target);
    }
    ...
}
```

**规则（C# 必须照抄）**：
1. 递归收集基类属性（`bases[].offset < 0` 的扩展基类跳过），基类在前；
2. **非 property 属性按 `offset` 升序排序**，property 属性排在最后并按名字排序（DS2 里 property 属性一律不序列化，被过滤掉）；
3. 过滤掉 `(flags & 2) != 0` 的属性；
4. 读取时**只按顺序读，不做任何 seek**，字段的绝对 offset 不参与定位。

由此得到 `StreamingGraphResource` 的实际属性流顺序（同一父类链内按 offset 升序；`RTTIRefObject.ObjectUUID` 来自基类，offset 16）：

`ObjectUUID` → `IsPacked`(32) → `TypeHashes`(40) → `TypeTableData`(72) → `LinkTableID`(144) → `LinkTableSize`(152) → `LocatorTable`(160) → `ArrayTable`(176) → `SpanTable`(192) → `Groups`(208) → `SubGroups`(224) → `RootUUIDs`(240) → `RootIndices`(256) → `Files`(336) → `PackFileOffsets`(352) → `PackFileLengths`(368) → `ObjectLocators`(384) → `PackFileUncompressedBlockSize`(400) → `PackFileMaxCompressedBlockSize`(404) → `UUIDLinkTable`(416)。

---

## 3. `StreamingGraphResource` 完整字段布局

types.json 原文：

```json
// odradek-game-ds2\src\main\resources\types.json:176170-176197（原文，按 JSON 顺序）
"StreamingGraphResource": { "kind": "compound", "version": 0, "flags": 16,
  "bases": [ {"type": "RTTIRefObject", "offset": 0} ],
  "attrs": [
    {"name": "IsPacked", "type": "bool", "offset": 32, "flags": 0},
    {"name": "Groups", "type": "Array_StreamingGroupData", "offset": 208, "flags": 0},
    {"name": "Files", "type": "Array_Filename", "offset": 336, "flags": 0},
    {"name": "TypeHashes", "type": "Array_uint64", "offset": 40, "flags": 0},
    {"name": "TypeTableData", "type": "Array_uint8", "offset": 72, "flags": 0},
    {"name": "LinkTableID", "type": "uint64", "offset": 144, "flags": 0},
    {"name": "LinkTableSize", "type": "int", "offset": 152, "flags": 0},
    {"name": "LocatorTable", "type": "Array_StreamingDataSourceLocator", "offset": 160, "flags": 0},
    {"name": "ArrayTable", "type": "Array_uint32", "offset": 176, "flags": 0},
    {"name": "SpanTable", "type": "Array_StreamingSourceSpan", "offset": 192, "flags": 0},
    {"name": "SubGroups", "type": "Array_uint32", "offset": 224, "flags": 0},
    {"name": "RootUUIDs", "type": "Array_GGUUID", "offset": 240, "flags": 0},
    {"name": "RootIndices", "type": "Array_uint32", "offset": 256, "flags": 0},
    {"name": "PackFileOffsets", "type": "Array_Array_int32", "offset": 352, "flags": 0},
    {"name": "PackFileLengths", "type": "Array_Array_int32", "offset": 368, "flags": 0},
    {"name": "PackFileUncompressedBlockSize", "type": "uint32", "offset": 400, "flags": 0},
    {"name": "PackFileMaxCompressedBlockSize", "type": "uint32", "offset": 404, "flags": 0},
    {"name": "ObjectLocators", "type": "Array_StreamingObjectLocator", "offset": 384, "flags": 0},
    {"name": "UUIDLinkTable", "type": "Array_GGUUID", "offset": 416, "flags": 0} ] }
```

基类：`RTTIObject`（`types.json:163331-163335`，无属性、无基类）；`RTTIRefObject`（`types.json:163372-163382`）继承 `RTTIObject`，唯一属性 `ObjectUUID: GGUUID @16 flags=5`（5 的 bit1 = 0 → **序列化**）。

C# 映射表（Java 侧类型来自 C#/Java 内置映射：`Array_uint8`→`byte[]`、`Array_uint32`→`int[]`、`Array_uint64`→`long[]`、`Array_Filename`→`List<string>`、`Array_T`→`List<T>`，见 `TypeSourceGenerator.java:231-238`）：

| 字段（JSON 名） | C# 属性名（normalizeCase） | C# 类型 | 序列化 | 用途 |
|---|---|---|---|---|
| `ObjectUUID` | `objectUUID` | `GGUUID`(16B) | 是 | 未被 odradek 使用（仅占位） |
| `IsPacked` | `isPacked` | `bool`(1B) | 是 | **未被使用** |
| `TypeHashes` | `typeHashes` | `long[]` | 是 | 全局类型哈希表，`typeTableData` 里的 uint16 索引查它 |
| `TypeTableData` | `typeTableData` | `byte[]` | 是 | 类型表正文（blob），见第 5 节 |
| `LinkTableID` | `linkTableID` | `ulong` | 是 | `Files` 数组下标：link table 存在哪个文件 |
| `LinkTableSize` | `linkTableSize` | `int` | 是 | link table 字节数（读取长度） |
| `LocatorTable` | `locatorTable` | `List<StreamingDataSourceLocator>` | 是 | locator 池，全局共享，按 group 的 `locatorStart/Count` 切片 |
| `ArrayTable` | `arrayTable` | `int[]` | 是 | **未被使用** |
| `SpanTable` | `spanTable` | `List<StreamingSourceSpan>` | 是 | span 池，按 group 的 `spanStart/Count` 切片 |
| `Groups` | `groups` | `List<StreamingGroupData>` | 是 | 所有 group |
| `SubGroups` | `subGroups` | `int[]` | 是 | 子 group id 的扁平池，按 `subGroupStart/Count` 切片 |
| `RootUUIDs` | `rootUUIDs` | `List<GGUUID>` | 是 | **未被使用** |
| `RootIndices` | `rootIndices` | `int[]` | 是 | 根对象索引池，按 `rootStart/Count` 切片 |
| `Files` | `files` | `List<string>` | 是 | 数据文件名列表（设备路径），见第 6 节 |
| `PackFileOffsets` | `packFileOffsets` | `List<int[]>` | 是 | **未被使用** |
| `PackFileLengths` | `packFileLengths` | `List<int[]>` | 是 | **未被使用** |
| `PackFileUncompressedBlockSize` | — | `uint` | 是 | **未被使用** |
| `PackFileMaxCompressedBlockSize` | — | `uint` | 是 | **未被使用** |
| `ObjectLocators` | `objectLocators` | `List<StreamingObjectLocator>` | 是 | **未被使用**（可作为 UUID→对象 的替代索引） |
| `UUIDLinkTable` | `uuidLinkTable` | `List<GGUUID>` | 是 | **未被使用** |

「未被使用」由对 `StreamingGraphImpl.java` 的实际引用与全仓 grep 确认（`objectLocators|rootUUIDs|arrayTable|isPacked|packFile|uuidLinkTable` 仅命中 `linkTableSize`）。

`GGUUID`（`types.json:116246-...`）：`kind: compound`，属性 `Data0`…`Data15`，全部 `uint8`，offset 0…15，共 **16 字节**。

---

## 4. `StreamingGroupData` 完整字段布局与用法

```json
// odradek-game-ds2\src\main\resources\types.json:176199-176221
"StreamingGroupData": { "kind": "compound", "version": 0, "flags": 16, "attrs": [
    {"name": "GroupID", "type": "int", "offset": 0, "flags": 0},
    {"name": "NumObjects", "type": "int", "offset": 4, "flags": 0},
    {"name": "GroupSize", "type": "int64", "offset": 8, "flags": 0},
    {"name": "SubGroupStart", "type": "uint32", "offset": 16, "flags": 0},
    {"name": "SubGroupCount", "type": "uint32", "offset": 20, "flags": 0},
    {"name": "RootStart", "type": "uint32", "offset": 24, "flags": 0},
    {"name": "RootCount", "type": "uint32", "offset": 28, "flags": 0},
    {"name": "SpanStart", "type": "uint32", "offset": 32, "flags": 0},
    {"name": "SpanCount", "type": "uint32", "offset": 36, "flags": 0},
    {"name": "TypeStart", "type": "uint32", "offset": 40, "flags": 0},
    {"name": "TypeCount", "type": "uint32", "offset": 44, "flags": 0},
    {"name": "LinkStart", "type": "uint32", "offset": 48, "flags": 0},
    {"name": "LinkSize", "type": "uint32", "offset": 52, "flags": 0},
    {"name": "LocatorStart", "type": "uint32", "offset": 56, "flags": 0},
    {"name": "LocatorCount", "type": "uint32", "offset": 60, "flags": 0},
    {"name": "ObjectUUIDs", "type": "Array_GGUUID", "offset": 64, "flags": 0} ] }
```

| JSON 名 | C# 名 | 类型 | 用法（行号引用 `odradek-game-ds2\...\storage\StreamingGraphImpl.java`，内部类 `GroupImpl`） |
|---|---|---|---|
| `GroupID` | `groupID` | `int` | `GroupImpl.id()` → `inner.groupID()` (:237-239)；被用作 `groupIds` 字典键 (:128-130) |
| `NumObjects` | `numObjects` | `int` | **未被使用**（对象数量由 `TypeCount` 决定） |
| `GroupSize` | `groupSize` | `long` | **未被使用** |
| `SubGroupStart` | `subGroupStart` | `uint` | `graph.subGroups.subList(subGroupStart, subGroupStart+subGroupCount)` (:248) |
| `SubGroupCount` | `subGroupCount` | `uint` | 同上 |
| `RootStart` | `rootStart` | `uint` | `rootIndices[rootStart .. rootStart+rootCount)` → `roots` (:231-233) |
| `RootCount` | `rootCount` | `uint` | 同上 |
| `SpanStart` | `spanStart` | `uint` | `graph.spans.subList(spanStart, spanStart+spanCount)` (:268) |
| `SpanCount` | `spanCount` | `uint` | 同上 |
| `TypeStart` | `typeStart` | `uint` | `graph.typeTable.subList(typeStart, typeStart+typeCount)` (:263) |
| `TypeCount` | `typeCount` | `uint` | **同时决定该 group 的对象个数**（`readSingleGroup` 用 `group.types().size()` 预建对象，:101） |
| `LinkStart` | `linkStart` | `uint` | `graph.links(linkStart)`，即 link table 中的字节偏移 (:278) |
| `LinkSize` | `linkSize` | `uint` | **未被使用**（link 迭代器一直读到 linkTable 末尾，见第 7 节） |
| `LocatorStart` | `locatorStart` | `uint` | `graph.locators.subList(locatorStart, locatorStart+locatorCount)` (:273) |
| `LocatorCount` | `locatorCount` | `uint` | 同上 |
| `ObjectUUIDs` | `objectUUIDs` | `List<GGUUID>` | **未被使用** |

`GroupImpl` 关键方法（节选，完整类见 `StreamingGraphImpl.java:222-280`）：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:227-234
private GroupImpl(StreamingGraphImpl graph, DS2.StreamingGroupData inner) {
    this.graph = graph;
    this.inner = inner;

    roots = Arrays.stream(graph.resource.rootIndices(), inner.rootStart(), inner.rootStart() + inner.rootCount())
        .boxed()
        .toList();
}

// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:247-279
public List<? extends StreamingGraph.Group> subGroups() {
    return graph.subGroups.subList(inner.subGroupStart(), inner.subGroupStart() + inner.subGroupCount());
}
public List<? extends StreamingGraph.Group> superGroups() {
    return graph.superGroups.getOrDefault(this, List.of());
}
public List<ClassTypeInfo> types() {
    return graph.typeTable.subList(inner.typeStart(), inner.typeStart() + inner.typeCount());
}
public List<StreamingGraph.Span> spans() {
    return graph.spans.subList(inner.spanStart(), inner.spanStart() + inner.spanCount());
}
public List<StreamingGraph.Locator> locators() {
    return graph.locators.subList(inner.locatorStart(), inner.locatorStart() + inner.locatorCount());
}
public Iterator<StreamingGraph.Link> links() {
    return graph.links(inner.linkStart());
}
```

`superGroups` 是反向索引，由 `subGroups()` 现算：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:132-141
private static Map<StreamingGraph.Group, List<StreamingGraph.Group>> computeGroupToSuperGroups(List<? extends StreamingGraph.Group> groups) {
    var result = new HashMap<StreamingGraph.Group, List<StreamingGraph.Group>>();
    for (StreamingGraph.Group group : groups) {
        for (StreamingGraph.Group subGroup : group.subGroups()) {
            result.computeIfAbsent(subGroup, _ -> new ArrayList<>()).add(group);
        }
    }
    result.replaceAll((_, parents) -> List.copyOf(parents));
    return Map.copyOf(result);
}
```

顶层 `subGroups` 数组先被整体映射成 Group 列表（顺序 = `SubGroups` 数组顺序）：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:143-147
private List<StreamingGraph.Group> computeSubgroups(DS2.StreamingGraphResource graph) {
    return IntStream.of(graph.subGroups())
        .mapToObj(this::group)
        .toList();
}
```

注意：`subGroups()` 返回的是**这个扁平池的切片**，切片元素是 group 对象；`superGroups` 再反查父 group。

---

## 5. typeTable 如何建立

### 5.1 完整代码

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:149-186（压缩）
var reader = BinaryReader.wrap(graph.typeTableData());   // typeTableData 是 Array_uint8 -> byte[]
var compression = reader.readInt();   // 必须 0
var stride      = reader.readInt();   // 必须 2  -> 每条记录 2 字节
var count       = reader.readInt();
var count2      = reader.readInt();   // 必须 == count
var unk10       = reader.readInt();   // 必须 1
// 上面任一断言不成立 -> IOException
var types = new ArrayList<ClassTypeInfo>(count);
for (int i = 0; i < count; i++) {
    var index = Short.toUnsignedInt(reader.readShort());   // uint16 小端索引
    var hash  = graph.typeHashes()[index];                 // Array_uint64 -> long[]
    var type  = factory.get(DS2TypeId.of(hash));           // 按 murmur3 hash 查类型
    types.add(type.asClass());
}
return List.copyOf(types);
```

### 5.2 二进制格式

`TypeTableData`（`Array_uint8` → `byte[]`）的内容，小端：

| 偏移 | 类型 | 值 | 说明 |
|---|---|---|---|
| 0 | `int32` | `compression` | 必须 0 |
| 4 | `int32` | `stride` | 必须 2（**每条记录 2 字节**） |
| 8 | `int32` | `count` | 条目数 |
| 12 | `int32` | `count2` | 必须 == `count` |
| 16 | `int32` | `unk10` | 必须 1 |
| 20 | `uint16[count]` | 索引 | 每条记录 2 字节，索引到 `TypeHashes` |

### 5.3 单条记录 → TypeId

- 每条 type 记录 = **2 字节的 uint16 小端索引**（`Short.toUnsignedInt(reader.readShort())`）。
- 索引到 `graph.typeHashes()`（`Array_uint64` → `long[]`），拿到 **uint64 hash**。
- `factory.get(DS2TypeId.of(hash))` 用这个 hash 查表：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\rtti\DS2TypeId.java:5-8
public record DS2TypeId(long hash) implements TypeId {
    public static TypeId of(long hash) {
        return new DS2TypeId(hash);
    }
}
```

- hash 的生成规则（DS2 专用）：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\rtti\DS2TypeFactory.java:18-23
@Override
protected TypeId computeTypeId(TypeInfo info) {
    var name = "00000001_" + info.name();
    var hash = DecimaHash.murmur3().hash(name).asLong();
    return DS2TypeId.of(hash);
}
```

即：`TypeId = murmur3_32(seed = 42, "00000001_" + TypeName)`，结果以 `long` 参与比较（`DecimaHash.java:8` 定义 `HashFunction.murmur3(42)`，`:13-15` 暴露）。**结果只有低 32 位有效，但比较的是 64 位 long 值**（见下方 [UNVERIFIED] 注）。

在 `AbstractTypeFactory` 构造时，`types.json` 里**每一个类型**都算出 TypeId 并建表：

```java
// odradek-rtti\src\main\java\sh\adelessfox\odradek\rtti\factory\AbstractTypeFactory.java:44-50
log.debug("Computing type ids");
for (TypeInfo info : context.getAll()) {
    TypeId id = computeTypeId(info);
    if (types.putIfAbsent(id, info) != null) {
        throw new IllegalStateException("Duplicate type id " + id + " for " + types.get(id) + " and " + info);
    }
}
```

### 5.4 全局 type 表 → 每个 group 的 type 切片

`typeTable` 的下标空间是**全局**的；`StreamingGroupData.TypeStart/TypeCount` 是**全局下标**：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:262-264
@Override
public List<ClassTypeInfo> types() {
    return graph.typeTable.subList(inner.typeStart(), inner.typeStart() + inner.typeCount());
}
```

**这个列表既是「对象类型表」也是「对象个数来源」**：`readSingleGroup` 按它逐个 `newInstance()`，然后按 span 顺序填充（第 8 节）。

> C# 移植注意：`typeStart/typeCount` 是 uint32，Java 直接当 int 用；若表长度超出 int 范围需注意。

---

## 6. Span / Locator / files 语义

### 6.1 三个 record 定义

```java
// odradek-game-decima\src\main\java\sh\adelessfox\odradek\game\decima\StreamingGraph.java:12-19
record Span(int fileIndex, int offset, int length) {
}

record Locator(int fileIndex, long offset) {
}

record Link(OptionalInt group, int index) {
}
```

### 6.2 Span 的来源与「字段顺序陷阱」

`StreamingSourceSpan` 定义见 6.3 的 JSON：**磁盘上的结构体是 12 字节：`fileIndexAndIsPatch`(4) → `length`(4) → `offset`(4)**，而 Java record 是 `(fileIndex, offset, length)` —— **顺序不同，不能按 record 顺序解析**。

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:116-120
private static List<StreamingGraph.Span> computeSpans(DS2.StreamingGraphResource graph) {
    return graph.spanTable().stream()
        .map(span -> new StreamingGraph.Span(span.fileIndexAndIsPatch() & 0x7fffffff, span.offset(), span.length()))
        .toList();
}
```

- `fileIndex` = `fileIndexAndIsPatch & 0x7fffffff`（**清掉最高位 = isPatch 标志**）。
- `offset` / `length` 是**该文件内的字节偏移/长度**。
- 实际读取：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:245-251
private byte[] getSpanData(StreamingGraph.Span span) throws IOException {
    return storage.read(getSpanFile(span), span.offset(), span.length());
}

private String getSpanFile(StreamingGraph.Span span) {
    return graph.files().get(span.fileIndex());
}
```

即：**`files()[span.fileIndex]` 文件在 `span.offset` 处的 `span.length` 字节，就是该 span 里所有对象的连续字节流**。

### 6.3 Locator

```json
// odradek-game-ds2\src\main\resources\types.json:176152-176168
"StreamingDataSource": { "kind": "compound", "version": 0, "flags": 0, "attrs": [
    {"name": "Channel", "type": "uint8", "offset": 0, "flags": 0},
    {"name": "Offset", "type": "int32", "offset": 4, "flags": 0},
    {"name": "Length", "type": "int32", "offset": 8, "flags": 0} ] },

"StreamingDataSourceLocator": { "kind": "compound", "version": 0, "flags": 0,
    "attrs": [ {"name": "Data", "type": "uint64", "offset": 0, "flags": 0} ] },

"StreamingSourceSpan": { "kind": "compound", "version": 0, "flags": 16, "attrs": [
    {"name": "FileIndexAndIsPatch", "type": "uint32", "offset": 0, "flags": 0},
    {"name": "Length", "type": "int", "offset": 4, "flags": 0},
    {"name": "Offset", "type": "int", "offset": 8, "flags": 0} ] }
```

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:110-114
private static List<StreamingGraph.Locator> computeLocators(DS2.StreamingGraphResource graph) {
    return graph.locatorTable().stream()
        .map(locator -> new StreamingGraph.Locator((int) (locator.data() & 0xffffff), locator.data() >>> 24))
        .toList();
}
```

解码：**一个 64 位 locator word**
- `fileIndex = data & 0xFFFFFF`（低 24 位）
- `offset = data >>> 24`（高 40 位，Java 的 `>>>` 是**无符号**右移）

回编码（写回 `StreamingDataSource.locator` 时）：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:162-175
if (dataSource.isValid()) {
    var locator = streamingLocators.next();
    ...
    dataSource.locator(locator.offset() << 24 | locator.fileIndex() & 0xffffff);
}
```

（Java 优先级：`<<` 高于 `|`，`&` 高于 `|`，所以等价于 `(offset << 24) | (fileIndex & 0xFFFFFF)`。）

对应的读取侧扩展：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\rtti\extensions\StreamingDataSourceExtension.java:6-19
default int fileId() {
    var dataSource = (DS2.StreamingDataSource) this;
    return (int) (dataSource.locator() & 0xffffff);
}

default int fileOffset() {
    var dataSource = (DS2.StreamingDataSource) this;
    return (int) (dataSource.locator() >>> 24);
}

default boolean isValid() {
    var dataSource = (DS2.StreamingDataSource) this;
    return dataSource.channel() != -1 /* EStreamingDataChannel.Invalid */ && dataSource.length() > 0;
}
```

`StreamingDataSource` 本体见 6.3 的 JSON（`Channel` / `Offset` / `Length`）。注意：`StreamingDataSource` **本身序列化时不含 locator**（locator 是由 streaming 读取过程从 `locatorTable` 里按顺序「配给」并写回的运行时字段，`StreamingObjectReader.java:157-176`）。真正的数据读取：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\game\DS2Game.java:93-99
public byte[] readDataSourceData(DS2.StreamingDataSource dataSource, int offset, int length) throws IOException {
    return readFileData(dataSource.fileId(), (long) dataSource.fileOffset() + offset, length);
}

private byte[] readFileData(int fileId, long offset, long length) throws IOException {
    return readFile(streamingGraph.files().get(fileId), offset, length);
}
```

### 6.4 `files()` 里是什么、怎么变成真实路径

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:106-108
private static List<String> computeFiles(DS2.StreamingGraphResource graph) {
    return Collections.unmodifiableList(graph.files());
}
```

- `Files` 是 `Array_Filename`，而 `Filename` 是原子类型别名：

```json
// odradek-game-ds2\src\main\resources\types.json:216095-216098
"Filename": {
    "kind": "atom",
    "base_type": "String"
},
```

- 所以 `files()` = `List<string>`；`Filename` 的序列化形式就是 Decima `String`：`int32 长度`（0 表示空串）→ `int32 CRC32 校验` → `长度` 字节 UTF-8，校验值为 `crc32(data) & 0x7fffffff`（`DS2TypeReader.java:171-190` 的 `StringReader`，不匹配直接抛 `IllegalArgumentException("String is corrupted")`）。

- **挂载与路径解析**：

`mount` 的路径解析与 DirectStorage/裸文件回退见 2.3；挂载成功后仅按文件名正则**打印日志**：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphStorage.java:21, 69-75
private static final Pattern PACKAGE_NAME = Pattern.compile("^package\\.(?<channel>\\d+)\\.(?<index>\\d+)\\.core");
...
var matcher = PACKAGE_NAME.matcher(file);
if (matcher.find()) {
    var channel = DS2.EStreamingDataChannel.valueOf(Byte.parseByte(matcher.group("channel")));
    log.info("Mounted file: {} ({})", file, channel);
} else {
    log.info("Mounted file: {}", file);
}
```

调用点：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\game\DS2Game.java:70-74
storage = new StreamingGraphStorage(this);
long start = System.currentTimeMillis();
storage.mountAll(graph.files());
long end = System.currentTimeMillis();
```

**要点（对 C# 移植很重要）**：
1. `files()` 的每个字符串被**原样**交给 `resolvePath`，而 `resolvePath` 要求 `<device>:<path>` 形式（`DS2Game.java:148-154`），否则抛 `IllegalArgumentException("Unknown device path: ...")`。所以 `Files` 里存的必然是**已带设备前缀的路径字符串**（例如 `source:LocalCacheWinGame/package.0.0.core` 之类）。
2. `PACKAGE_NAME` 正则（`^package.` 锚定行首）**只用于日志里打印 channel**，不影响文件解析；若 `Files` 里带 `source:` 前缀，该正则永远不会命中（`matcher.find()` 因 `^` 只在串首生效）。
3. `mount` 失败只是 `log.warn` 并跳过；真正读的时候若文件未挂载会抛 `IllegalArgumentException("Can't resolve file: ...")`：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphStorage.java:98-104
private BinaryReader resolve(String file) {
    BinaryReader reader = files.get(file);
    if (reader == null) {
        throw new IllegalArgumentException("Can't resolve file: " + file);
    }
    return reader;
}
```

4. 每个文件先按 DirectStorage（LZ4 分块归档）尝试，失败则当裸文件（见 2.3）。C# 端至少要支持裸文件 + DirectStorage(LZ4 block) 两条路。

`EStreamingDataChannel`（`types.json:214101-...`）：`kind: enum`，`size: 1`（1 字节），`0 = ObjectChannel`，`1..N = Language*`。

---

## 7. linkTable / `links()` 编码

### 7.1 link table 从哪来

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:188-194
private static byte[] readLinkTable(
    DS2.StreamingGraphResource graph,
    StreamingGraphStorage storage
) throws IOException {
    var file = graph.files().get(Math.toIntExact(graph.linkTableID()));
    return storage.read(file, 0, graph.linkTableSize());
}
```

- **文件** = `Files[LinkTableID]`（`LinkTableID` 是 uint64，被 `Math.toIntExact` 转成 int 下标）。
- **偏移恒为 0**，**长度 = `LinkTableSize`**。
- 整个 link table 一次性读进内存（`byte[] linkTable`）。

### 7.2 迭代器

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:80-94
@Override
public Iterator<StreamingGraph.Link> links(int position) {
    var buffer = ByteBuffer.wrap(linkTable, position, linkTable.length - position);
    return new Iterator<>() {
        @Override
        public boolean hasNext() {
            return buffer.hasRemaining();
        }

        @Override
        public StreamingGraph.Link next() {
            return readLink(buffer);
        }
    };
}
```

**注意**：迭代器读到 `linkTable` 的**物理末尾**才停，**不使用** `StreamingGroupData.LinkSize`；`linkStart` 只是起始字节偏移。

### 7.3 单条 link 的编码

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingGraphImpl.java:196-220
private static StreamingGraph.Link readLink(ByteBuffer buffer) {
    OptionalInt linkGroup;
    int linkIndex;

    int first = buffer.get();
    if ((first & 0x40) != 0) {
        linkGroup = OptionalInt.of(readVarInt(buffer, first & 0xbf));
        linkIndex = readVarInt(buffer, buffer.get());
    } else {
        linkGroup = OptionalInt.empty();
        linkIndex = readVarInt(buffer, first & 0xbf);
    }

    return new StreamingGraph.Link(linkGroup, linkIndex);
}

private static int readVarInt(ByteBuffer buffer, int initial) {
    int temp = initial;
    int value = initial & 0x7f;
    while ((temp & 0x80) != 0) {
        temp = buffer.get();
        value = (value << 7) | (temp & 0x7f);
    }
    return value;
}
```

**解码规则（明确回答「大端还是小端」）：varint 是「大端 / MSB-first / base-128」**——因为 `value = (value << 7) | (temp & 0x7f)`，先出现的 7 位是高位。（对比：LEB128 是小端，`value |= (temp & 0x7f) << shift`。）

字段顺序与位语义：

| 字节位置 | 内容 |
|---|---|
| 第 1 字节 | bit7(0x80) = varint 续接标志；bit6(0x40) = **是否存在 group**；bit5..0 = 第一个数值的低 6 位 |
| 若 bit6 置位 | 先读 **group id 的 varint**（第 1 字节续用 `first & 0xbf`，即低 6 位数据 + bit7 续接），再读 **index 的 varint**（从下一个字节开始） |
| 若 bit6 未置位 | 第 1 字节的低 6 位就是 **index varint** 的起始，随后按 bit7 续接 |

即：
- `hasGroup`：`(b1 & 0x40) != 0`
- `group`（可选）的 varint 初值 = `b1 & 0xBF`（0xBF = `1011_1111`，清掉 bit6，保留 bit7 作续接）；`readVarInt` 内部再 `& 0x7F` 取值，所以实际数据位是 `b1 & 0x3F`。
- `index` 的 varint：有 group 时从**下一个字节**开始（整字节），无 group 时复用 `b1 & 0xbf`。

### 7.4 Link 的语义（消费方）

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:178-205
private Object resolveLink(PointerTypeInfo info) {
    if (!resolveStreamingLinksAndLocators) return null;

    var result = streamingLinks.next();
    var linkGroup = result.group();
    int linkIndex = result.index();

    var pointerType = info.pointerType();
    if (pointerType.equals("StreamingRef")) {
        if (linkGroup.isPresent()) {
            // If linkGroup != -1, then it's the id of the group; it's an equivalent of doing graph.group(linkGroup)
            return new StreamingRef<>(new ObjectId(linkGroup.orElseThrow(), linkIndex));
        } else {
            // No idea how to resolve it otherwise. Presumably points to a runtime singleton?
            return null;
        }
    }

    GroupResult group;
    if (linkGroup.isPresent()) {
        group = currentSubGroups.get(linkGroup.orElseThrow());   // Seems to reference subgroups
    } else {
        group = currentGroup;                                     // References the current group being read
    }

    var object = group.objects().get(linkIndex);
    ...
```

- `StreamingRef`：`linkGroup` 是**全局 group id**，`index` 是组内对象下标 → 形成 `ObjectId(groupId, index)`。
- 其它指针类型（`Ref`/`WeakPtr`/`cptr`）：`linkGroup` 是**当前 group 的 `subGroups()` 列表下标**（不是 group id！），缺省表示当前 group 自身；`index` 是该 group 内对象下标。
- 指针是**顺序消费**的：每次遇到一个需要解析的指针就从 `streamingLinks` 取一条 (`streamingLinks.next()`)。

> `PointerTypeInfo.pointerType()` 的取值来自 types.json 的 pointer 类型名（`Ref_*`、`StreamingRef_*`、`WeakPtr_*`、`cptr_*`、`UUIDRef_*`），见 `TypeSourceGenerator.java:239-246` 与 `module-info.java:46-50`。

指针读取入口：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:146-155
@Override
protected Object readPointer(PointerTypeInfo info, BinaryReader reader, TypeFactory factory) throws IOException {
    if (!reader.readBool(BoolFormat.BYTE)) {
        return null;
    } else if (info.pointerType().equals("UUIDRef")) {
        return new UUIDRef<>((DS2.GGUUID) readCompound(factory.get("GGUUID").asClass(), reader, factory));
    } else {
        return resolveLink(info);
    }
}
```

**关键**：每个指针在数据流里是 **1 字节 bool**（`BoolFormat.BYTE`，0/1，其它值抛异常 `BinaryReader.java:151-155`）。非 `UUIDRef` 且非 0 时，才会消耗 link table 的一条记录。

---

## 8. 读对象的完整调用链

### 8.1 接口默认实现

```java
// odradek-game-decima\src\main\java\sh\adelessfox\odradek\game\decima\DecimaGame.java:18-30
default List<TypedObject> readGroup(int groupId) throws IOException {
    return readGroup(groupId, true);
}
...
List<TypedObject> readGroup(int groupId, boolean readSubgroups) throws IOException;
```

### 8.2 DS2Game

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\game\DS2Game.java:101-106
@Override
public List<TypedObject> readGroup(int groupId, boolean readSubgroups) throws IOException {
    synchronized (streamingReader) {
        return streamingReader.readGroup(groupId, readSubgroups).objects();
    }
}
```

### 8.3 StreamingObjectReader：三级调用

**(a) 公开入口 → (b) 带缓存的 id 版 → (c) 真正的组版**

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:55-98（压缩）
public GroupResult readGroup(int id, boolean readSubgroups) throws IOException {
    return readGroup(id, new HashMap<>(), readSubgroups);
}

private GroupResult readGroup(int id, Map<Integer, GroupResult> cache, boolean readSubgroups) throws IOException {
    var group = Objects.requireNonNull(graph.group(id), () -> "Group not found: " + id);
    var result = cache.get(group.id());
    if (result == null) {
        depth++;
        result = readGroup(group, readSubgroups);
        cache.put(result.group.id(), result);
        depth--;
    }
    return result;
}

private synchronized GroupResult readGroup(StreamingGraph.Group group, boolean readSubgroups) throws IOException {
    var subGroups = new ArrayList<GroupResult>(group.subGroups().size());
    if (readSubgroups) {
        for (StreamingGraph.Group subGroup : group.subGroups()) {
            subGroups.add(readGroup(subGroup, true));   // 子调用恒为 true（深度优先）
        }
    }

    currentSubGroups = subGroups;
    resolveStreamingLinksAndLocators = readSubgroups;

    var result = cache.get(group.id());
    if (result == null) {
        result = readSingleGroup(group);
        cache.put(group.id(), result);
    }
    return result;
}
```

要点：
- `readSubgroups == true` 时**先深度优先递归读完所有子 group**（子调用恒为 `true`），把结果按 `subGroups()` 顺序存进 `currentSubGroups`——**这正是 link 里「子 group 下标」的解析依据**（第 7.4 节）。
- `resolveStreamingLinksAndLocators = readSubgroups`：若本次不读子 group，则 group 内的指针/locator **一律解析为 null 且不消费 link/locator 游标**：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:157-161
private void resolveStreamingDataSource(DS2.StreamingDataSource dataSource) {
    if (!resolveStreamingLinksAndLocators) {
        return;
    }
```

- 缓存有两层：`LruWeakCache<Integer, GroupResult> cache`（成员，容量 5000，跨调用复用，`StreamingObjectReader.java:28`）和带缓存 map 的局部 `Map<Integer, GroupResult>`（仅在一次顶层调用内有效，防重入）。
- `DS2Game.readGroup` 在 `streamingReader` 上 `synchronized`；`readGroup(Group, boolean)` 方法本身也 `synchronized`。C# 端需要等价的可重入/单线程保护。

### 8.4 真正的对象填充

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:100-135（节选）
var objects = new ArrayList<TypedObject>(group.types().size());
for (ClassTypeInfo type : group.types()) {
    objects.add(type.newInstance());
}
var result = new GroupResult(group, objects);
currentGroup = result;
streamingLinks = group.links();
streamingLocators = group.locators().iterator();

int index = 0;
for (StreamingGraph.Span span : group.spans()) {
    var data = getSpanData(span);              // storage.read(files[span.fileIndex], span.offset, span.length)
    var reader = BinaryReader.wrap(data);
    while (reader.remaining() > 0) {
        var object = objects.get(index++);
        fillCompound(object.getType(), reader, factory, object);   // 无头、无长度前缀
    }
}
return result;
```

**数据来源链条（每个 object 的字节从哪来）**：

1. `objects` 列表按 `group.types()`（= `typeTable[typeStart .. typeStart+typeCount)`）长度预建，**顺序即对象顺序**。
2. 遍历 `group.spans()`（= `spanTable[spanStart .. spanStart+spanCount)`，**顺序即 spanTable 数组顺序**）。
3. 每个 span → `storage.read(files[span.fileIndex], span.offset, span.length)` → `byte[] data`。
4. `BinaryReader.wrap(data)` 从 0 开始顺序读，`while (reader.remaining() > 0)` 循环逐个对象；
5. 对第 `index` 个对象调用 `fillCompound(objectType, reader, factory, object)` —— **对象类型的属性流直接拼接在 span 里，没有长度前缀、没有类型 id、没有对齐**。
6. `StreamingObjectReader` 覆写 `fillCompound`，在读完一个复合对象后再解析它内部的 `StreamingDataSource`（用于把 locator 写回）：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:137-144
@Override
protected void fillCompound(ClassTypeInfo info, BinaryReader reader, TypeFactory factory, Object target) throws IOException {
    super.fillCompound(info, reader, factory, target);

    if (target instanceof DS2.StreamingDataSource dataSource) {
        resolveStreamingDataSource(dataSource);
    }
}
```

推论（对 C# 实现有约束意义）：
- 对象**不能跨 span**（每个 span 用独立 reader 解析，跨边界会在 `fillCompound` 中途 EOF）。
- 若 span 里剩余的字节不足以构成一个对象，或对象数 ≠ `typeCount`，会分别导致 EOF / `IndexOutOfBoundsException`——代码里没有额外校验。
- `NumObjects` 字段被忽略，**对象个数 = `TypeCount`**。

`readSubgroups` 的含义（总结）：控制是否**递归读取该 group 的全部子 group**，并同时决定是否启用 link/locator 解析（`resolveStreamingLinksAndLocators`）。`false` 用于只关心 root 对象、不关心指针解析的场景。

`readObject(ObjectId)` / `readObject(groupId, objectIndex)` 的实现（`DecimaGame.java:32-46`）：

```java
// odradek-game-decima\src\main\java\sh\adelessfox\odradek\game\decima\DecimaGame.java:32-46
default TypedObject readObject(ObjectId objectId) throws IOException {
    return readObject(objectId.groupId(), objectId.objectIndex());
}
...
default TypedObject readObject(int groupId, int objectIndex) throws IOException {
    return readGroup(groupId).get(objectIndex);
}
```

---

## 9. 每个 object 的二进制头

**结论：DS2 的 streaming 数据里，每个 object 没有任何头。** 类型来自全局 type 表，字节流就是该类型属性的裸拼接。

证据：

```java
// odradek-game-ds2\src\main\java\sh\adelessfox\odradek\game\ds2\storage\StreamingObjectReader.java:100-118
var objects = new ArrayList<TypedObject>(group.types().size());
for (ClassTypeInfo type : group.types()) {
    objects.add(type.newInstance());
}
...
int index = 0;
for (StreamingGraph.Span span : group.spans()) {
    var data = getSpanData(span);
    var reader = BinaryReader.wrap(data);

    while (reader.remaining() > 0) {
        var object = objects.get(index++);
        ...
        fillCompound(object.getType(), reader, factory, object);
    }
}
```

**唯一的「头」是文件级资源头**（只有 `streaming_graph.core` 有），见 2.2：`uint64 typeHash` + `int32 size` + body + `int32 numLinks(==0)`，由 `DS2TypeReader.readObject` 解析（`DS2TypeReader.java:23-43`），**不用于 span 里的对象**。

（另有一个容易误判的点：types.json 里属性带 `offset`，例如 `RTTIRefObject.ObjectUUID` 在 offset 16。这些 offset **只用于排序**，`AbstractTypeReader.fillCompound` 从不按 offset 定位，也从不跳过 padding —— 见第 2.4 节的 `createDecodingPlan`/`fillCompound` 源码。）

---

## 10. 无法从源码确认的点（`[UNVERIFIED]`）

1. **`[UNVERIFIED]` `Files` 数组中字符串的精确字面形式。**
   代码要求它必须能被 `DS2Game.FileSystem.resolve` 解析（须含 `<device>:` 前缀，`DS2Game.java:146-155`），但仓库里没有真实的 `streaming_graph.core` 样本。`PACKAGE_NAME` 正则（`StreamingGraphStorage.java:21`）假设了 `package.<channel>.<index>.core` 这样的裸名，与 `resolve` 的前缀要求冲突；该正则只影响日志，不影响解析。**移植时应写成：按 `:` 拆设备前缀 → 展开；展开失败时再尝试当作 `LocalCache<Platform>\package\...` 的相对名（防御式），并从文件名里尽力解析 channel 仅用于日志。**

2. **`[UNVERIFIED]` `typeTableData` 头 5 个 int 的语义。**
   代码只断言 `compression==0 / stride==2 / count==count2 / unk10==1`（`StreamingGraphImpl.java:157-174`）。`unk10` 的含义未知（`compression`、`stride` 之外的第三个 count 类字段名也不明）。

3. **`[UNVERIFIED]` `TypeId` 的 64 位比较细节。**
   `DecimaHash.murmur3().hash(name).asLong()` 返回 long（`DS2TypeFactory.java:21`），但 `HashCode.asLong()` 对 32 位哈希的具体填充方式（高位补 0 还是符号扩展）来自外部依赖 `wtf.reversed.toolbox.hash`，**本地不可见**（`.gitmodules` 指向 `https://github.com/reversed-wtf/reversed-toolbox`，`lib/reversed-toolbox` 为空目录，无网络访问）。
   → `typeHashes` 里的 uint64 与 `TypeId` 的比较**很可能是「低位 32 位相等」**，但需在真实数据上验证。
   同理，`HashFunction.hash(String)` 的字符串编码（**推测 UTF-8**）也未能从源码确认。

4. **`[UNVERIFIED]` `CRC32` 参数对应关系。**
   `DecimaHash.CRC32 = new CRCAlgorithm(32, 0x1edc6f41, 0, true, true, 0)`（`DecimaHash.java:7`），参数含义来自 toolbox。Decima `String` 的校验和是 `crc32 & 0x7fffffff`（`DS2TypeReader.java:183-186`）。多项式 `0x1EDC6F41` 是 CRC-32C 的多项式，但 reflect/init/xorout 的具体组合需实测。

5. **`[UNVERIFIED]` `DirectStorageReader` 头部格式。**
   `DirectStorageReader.java:50-60` 显示 chunk 表项为 `int64 offset, int64 compressedOffset, int32 size, int32 compressedSize, uint8 type(==3 = lz4), 7 bytes padding`，但 `Header` 结构体（`DirectStorageReader.java:26`）在本次未逐行读完，且 LZ4 block 解压参数（`Decompressor.lz4Block()`）来自 toolbox。若 DS2 的 `package.*.core` 是裸文件，C# 端可先只实现裸文件路径。

6. **`[UNVERIFIED]` `StreamingGraphResource.Files` 与 `LinkTableID` 的对应关系是否允许 `LinkTableID` 指向“同一个文件”。**
   `readLinkTable` 从 `offset = 0` 读 `LinkTableSize` 字节（`StreamingGraphImpl.java:192-193`），说明 link table 独占文件开头。若真实数据里 link table 与对象数据同处一个文件，则 `offset=0` 的假设需再验证。

7. **`[UNVERIFIED]` `ObjectUUIDs` / `ObjectLocators` / `RootUUIDs` / `UUIDLinkTable` 的格式与用途。**
   这些字段在 odradek 中**完全未被读取**（全仓 grep 无引用），因此其二进制布局虽在 types.json 里有定义（`StreamingObjectLocator` 见 `types.json:176222-176233`），但语义（UUID↔对象映射？）无代码佐证。

```json
// odradek-game-ds2\src\main\resources\types.json:176222-176233  (磁盘 32 字节)
"StreamingObjectLocator": { "kind": "compound", "version": 0, "flags": 16, "attrs": [
    {"name": "ObjectUUID", "type": "GGUUID", "offset": 0, "flags": 0},
    {"name": "TypeIndex", "type": "uint16", "offset": 16, "flags": 0},
    {"name": "Reserved", "type": "uint16", "offset": 18, "flags": 0},
    {"name": "FileIndex", "type": "int", "offset": 20, "flags": 0},
    {"name": "Offset", "type": "int", "offset": 24, "flags": 0},
    {"name": "Length", "type": "int", "offset": 28, "flags": 0} ] }
```
   （`typeIndex` 很可能正是全局 type 表下标，与名字相符 —— 但无代码佐证。）

8. **`[UNVERIFIED]` span 与 group 的边界关系。**
   代码隐含「对象不跨 span、span 内对象数 = 该 span 覆盖的 type 条目数」，但没有断言；`NumObjects` 被忽略也说明 `TypeCount` 才是权威。

9. **`[UNVERIFIED]` `EStreamingDataChannel.Invalid` 的实际数值。**
   `StreamingDataSourceExtension.java:18` 注释写 `-1`，但 types.json 的 `EStreamingDataChannel` 段（`types.json:214101` 起）在本次只读到 value 20，未确认是否存在 `Invalid = -1` 或 `0xFF`。

10. **`[UNVERIFIED]` `ProductVersion.find` 的版本探测与本次任务无关**，`DS2Game` 只把它写进日志（`DS2Game.java:51-59`），不参与识别或解析。

---

## 附：C# 移植的落地清单（基于以上事实）

1. **Game 探测**：`File.Exists(root + "\\DS2.exe")`；入口资源 = `root + "\\LocalCacheWinGame\\package\\streaming_graph.core"`。
2. **资源头**：`ulong typeHash; int size; <body>; int numLinks(必须0)`，全小端；body 长度必须恰好等于 `size`。
3. **类型表**：`Dictionary<ulong /*murmur3_32(seed 42)("00000001_"+Name)*/, TypeInfo>`；此表由 types.json 全量构建。
4. **属性顺序**：递归基类（跳过 `offset<0` 的 extension 基类）→ 过滤 `(flags & 2) != 0` → 非 property 按 `offset` 升序（property 排最后，DS2 里都被过滤）。
5. **typeTable blob**：5 × int32 头（0,2,count,count,1）+ `count` × uint16 索引 → `TypeHashes[index]`。
6. **span 结构体**：`uint fileIndexAndIsPatch; int length; int offset;`（注意 length 在前！）；`fileIndex = fileIndexAndIsPatch & 0x7FFFFFFF`。
7. **locator**：`ulong data`；`fileIndex = (int)(data & 0xFFFFFF)`，`offset = (long)(data >> 24)`（无符号）。
8. **link varint**：**大端 base-128**（`v = (v << 7) | (b & 0x7F)`）；首字节 bit6 = 有 group，bit7 = 续接。
9. **对象**：无头、无长度；按 `group.typeCount` 个对象，按 span 顺序、span 内顺序填充。
10. **指针**：数据流里 1 字节 bool；`UUIDRef` 后跟 16 字节 GGUUID；其余非 0 指针从 link table 顺序取一条。
