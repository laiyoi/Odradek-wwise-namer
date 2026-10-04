# Odradek-wwise-namer

<p align="center">
  <a href="README.md">中文版</a>
</p>

An audio file naming and export tool based on [Odradek](https://github.com/ShadelessFox/odradek), designed for **Death Stranding 2**.

## Introduction

[Odradek](https://github.com/ShadelessFox/odradek) is a Horizon Forbidden West asset viewer and extractor, a reincarnation of [Decima Workshop](https://github.com/ShadelessFox/decima). It is designed for modders working with Decima engine games.

This project builds two things on top of it:

1. **Exporting assets** — it reads `streaming_graph.core` straight from the DS2 installation, searches objects by type and writes them out as JSON.
   **No Odradek GUI and no `odradek.exe` required.**
   This part is done by [OdradekSharp/](OdradekSharp/) in this repository (another agent's C# port of Odradek; both the graph statistics and the export results were verified against Odradek).
2. **Naming and exporting audio** — BNK extraction → TXTP generation with wwiser → mapping build → WAV export, plus text labeling of unused WEM files.

The whole pipeline lives in the **WemLabeler** GUI, in two tabs: **"Extract Audio" comes first, "Label Audio" second**.

> The 7 Python scripts are still kept under `pyscript/` as the original reference implementation (they read and write exactly the same file formats), but they are no longer the main path.

## Prerequisites

| Requirement | Purpose |
| --- | --- |
| **Death Stranding 2** game install | Source for the asset export (step 0) — the folder containing `DS2.exe` |
| [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | **Building** WemLabeler |
| [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) | **Running** WemLabeler (when using the published single-file build) |
| Python 3.x | **Only needed for ② "Generate TXTP with wwiser"**; the interpreter is found on `PATH` automatically, no configuration |
| [wwiser](https://github.com/bnnm/wwiser) · [vgmstream](https://github.com/vgmstream/vgmstream) | Downloaded **automatically** by the app, see below |

**No longer required:** [Odradek](https://github.com/ShadelessFox/odradek) itself or `odradek.exe`.

### Where the tools are downloaded to

Both external tools go into the **`utils\` folder next to the exe** (not the `utils\` in the project root):

| Tool | Source | Destination |
| --- | --- | --- |
| vgmstream | latest release of `vgmstream/vgmstream-releases` | `<exe>\utils\vgmstream-win\` |
| wwiser | `wwiser.pyz` | `<exe>\utils\` |

Put `wwnames.db3` next to `wwiser.pyz` (i.e. in `<exe>\utils\`) and the generated txtp files will carry readable event names.
You can also trigger the download manually with **"Download tools (vgmstream + wwiser)"**; the **"Utils dir"** button opens that folder.

## Usage Steps

Everything happens in WemLabeler's two tabs:

- **Extract Audio** (opened by default): the whole pipeline — walk through the buttons in order (0 → ① → ② → ③ → ④)
- **Label Audio**: load a CSV, listen, write labels

The mapping between buttons and the original Python scripts is in [Original Python Scripts (Reference)](#original-python-scripts-reference).

### ⓪ Export assets (read game files)

Click **"⓪ Export assets (read game files)"**.

- Reads `<game root>\LocalCacheWinGame\package\streaming_graph.core` directly
- A game folder is recognised by having `DS2.exe` in it; **"Auto-find"** scans the Steam libraries on every drive
- Searching objects by type is a **pure metadata operation, no deserialization**; only the matched objects are read and written out as JSON
- **Incremental**: files that already exist are skipped, so an interrupted run just continues where it stopped

Measured graph and object counts (DS2 2026-10 build):

| Item | Value |
| --- | --- |
| Graph size | 79,323 groups / 5,354,196 objects / 241 files |

| Type | Objects | Output folder |
| --- | --- | --- |
| WwiseWemResource | 7,838 | `<project root>\WemResJson` |
| WwiseBankResource | 74 | `<project root>\BankRes` |
| GraphSoundResource | 5,700 | `<project root>\GraphSoundRes` |
| GraphProgramResource | 25,663 | `<project root>\GraphPgmRes` |
| NodeConstantsResource | 26,385 | `<project root>\NodeConstRes` |
| WwiseID | 7,000 | `<project root>\WwiseID` |

These counts are **exactly equal** to the number of files Odradek actually exported.

### ① Extract BNK from BankRes

Click **"① Extract BNK from BankRes"**. The app will:

- Read the JSON files in `BankRes`
- Decode the Base64-encoded `BankData`
- Fix the Wwise Bank data alignment
- Write the `.bnk` files into `Extracted_Banks\`

### ② Generate TXTP with wwiser

Click **"② Generate TXTP with wwiser"**. The command it actually runs is:

```bash
python "<exe>\utils\wwiser.pyz" -g -go "<project root>\Extracted_Banks\txtp" "<project root>\Extracted_Banks\*.bnk"
```

The generated `.txtp` files land in `Extracted_Banks\txtp\`.

> This step needs Python 3.x on `PATH`. It used to require opening wwiser manually and clicking "Generate TXTP".

### ③ Build Audio Mapping

Click **"③ Build Audio Mapping"**. It parses all JSON and TXTP files and produces `sound_wem_mapping_export.json`:

- Associates GraphSoundResource / GraphProgramResource / NodeConstantsResource / WwiseID
- Records missing WEM files to `missing_wem_files.csv`
- Read-only, very fast

### ④ Export Audio from Mapping

Click **"④ Export Audio from Mapping"**. It exports WAV files from the mapping table already built:

- Skips the repeated JSON parsing
- Decoded by vgmstream, with resume support (progress is kept in `export_progress.json`)
- Defaults to `<project root>\Exported_Audio`, changeable in the path settings

### Export WEM audio (~10 GB)

Click **"Export WEM audio (~10GB)"**.

- Pulls the raw bytes out of every `WwiseWemResource`: streaming ones are read through the `StreamingDataSource` Locator from the package files, embedded ones from `WemData`
- The extracted bytes are **byte-identical** to the existing `.wem` files (header is `RIFF....WAVEfmt `)
- **About 10 GB** — clicking the button pops up a warning and asks where to save ("Yes" = the default folder from the config, "No" = pick a folder, "Cancel" = abort)
- Also incremental: `.wem` files already present in the target folder are skipped

### Analyze Unused WEM (optional)

Click **"Analyze Unused WEM"**. The app will:

- Read `missing_wem_files.csv` (the list of unused WEM files)
- Parse `banks.xml` to find which banks contain these WEMs
- Parse the `WwiseID` folder for the corresponding WwiseIDs
- Parse the `WemResJson` folder for the original WwiseWemResource files
- Produce `unused_wem_with_banks.csv`, format described in [Format of unused_wem_with_banks.csv](#format-of-unused_wem_with_bankscsv)

You must set the **Streaming WEM folder** first, otherwise the app **fails with an error** instead of writing a pile of empty paths.

### Label Audio

Switch to the **"Label Audio"** tab and click **"Open CSV..."** to load `unused_wem_with_banks.csv`. This tab provides:

- A file list on the left; labels are **written back into the CSV you opened** automatically
- Real-time WEM audio preview (vgmstream decode + WASAPI playback)
- **One-click playback of the txtp that owns a WEM**: select an entry and click "Play owning txtp" (or press `Ctrl+T`); the app reverse-looks-up the txtp referencing that WemID and plays it, with **no manual drag-and-drop required**. If several txtp reference it, a picker appears
- **"Open audio folder"**: jumps straight to the folder containing the selected WEM
- Waveform visualization with click-to-seek
- **"Export labels CSV..."** saves a copy of the labeling result; **"Export labeled WAV..."** batch-exports the labeled WAVs (named after the labels)
- Chinese / English UI switching

### Other buttons

| Button | Purpose |
| --- | --- |
| Rebuild Txtp Index | Rebuilds the WemID → txtp index after the txtp folder changes |
| Export by Event ID | Enter an Event ID and export the audio it references (cached in `wem_map_cache.json`) |

## Path Settings

Every path at the top of the **Extract Audio** tab **can be typed in directly**; edits are written back to `config.json` immediately, so clicking "Browse..." is optional.

| Path | Description |
| --- | --- |
| Project root | Everything else is derived from it |
| Audio output folder | Where ④ writes the WAV files |
| Streaming WEM folder | Where the `.wem` files live (the `WemPath` column depends on it) |
| txtp folder | Defaults to `<project root>\Extracted_Banks\txtp` |
| vgmstream | A manually set path wins, otherwise the one in `utils\` next to the exe |
| Game root | Input for the asset export, the folder containing `DS2.exe` |
| WEM audio folder | Output location for "Export WEM audio" |

- **"Browse..."** uses .NET 8+ `OpenFolderDialog` — the standard Explorer-style dialog (address bar, navigation pane, search box), not the legacy tree dialog
- **"Auto-find"** is available for the game root
- **The project root is auto-detected**: a folder counts as a hit if it contains any of `GraphSoundRes` / `BankRes` / `WemResJson` / `Extracted_Banks`. If detection fails, the app asks you to pick one once and remembers it

## Format of unused_wem_with_banks.csv

Base columns (**the uninformative `IsStreaming` has been dropped**):

```
WemID,Coord,JsonFile,WemFile,WemPath,FoundInBankRes,TxtpFiles
```

| Field | Description |
| --- | --- |
| WemID | ID of the WEM file |
| Coord | WemRes coordinate (e.g. `1604:4570`) |
| JsonFile | Matching WwiseWemResource JSON filename |
| WemFile | WEM filename |
| WemPath | Full path of the WEM file (depends on a correct **Streaming WEM folder**) |
| FoundInBankRes | Whether referenced by BankRes `WemIDs` (是 / 否) |
| TxtpFiles | txtp files referencing this WEM, separated by `;` |

Regenerating it never loses information:

- **Every column other than the base columns and the dropped one (`IsStreaming`) is carried over as-is, matched by WemID** — the `Label` / `Duration` / `Channel` columns, or any column you added yourself
- Rows in the old file that are **no longer "unused" are carried over whole, at the end of the file**, labels included
- The log reports how many of each were carried over

## Original Python Scripts (Reference)

The 7 scripts under `pyscript/` are kept; this project started with them. They read and write exactly the same file formats as WemLabeler, so the two can be mixed — but they are **no longer the main path**; the buttons below are.

| WemLabeler button | Equivalent script |
| --- | --- |
| ⓪ Export assets (read game files) | — (new; replaces the manual Odradek export) |
| ① Extract BNK from BankRes | `extract_bnk_from_json.py` |
| ② Generate TXTP with wwiser | the manual "Generate TXTP" click in wwiser |
| ③ Build Audio Mapping | `export_sounds.py 1` |
| ④ Export Audio from Mapping | `export_sounds.py 2` |
| Export by Event ID | `export_by_id.py` |
| Analyze Unused WEM | `link_unused_wem.py` |

The remaining scripts: `build_audio_manifest.py` (build an audio manifest), `fix_negative_ids.py` (fix negative IDs in JSON), `match.py` (recover original names by audio-content MD5).

> Those scripts still contain hard-coded paths (e.g. `BASE_DIR`); edit them if you want to use them. They run from the project
> root, e.g. `cd <project root>` then `python pyscript\extract_bnk_from_json.py`
> (`extract_bnk_from_json.py` uses the relative path `./BankRes`).

## Project Structure

```
Odradek-wwise-namer/
├── OdradekSharp/            # C# reader that reads Decima game files directly (line-by-line port of Odradek)
│   ├── Data/                # types.json (8.24 MB) + extensions.json — type schema, required at runtime
│   ├── Ds2/                 # streaming graph, object reader, facade (DecimaGame)
│   ├── Rtti/                # type table, deserialization, the 14 DS2 callbacks
│   ├── Io/                  # DSAR container + LZ4 + little-endian reader / murmur3 / CRC-32C
│   ├── Export/              # JSON export, byte-identical to Odradek
│   └── Program.cs           # standalone CLI (info / types / find / read / dump / hex), for debugging
├── WemLabeler/              # WPF app: Extract Audio + Label Audio
│   ├── MainWindow.xaml      # layout of the two tabs
│   ├── MainWindow.xaml.cs   # labeling tab logic (playback / labeling / export)
│   ├── MainWindow.Extract.cs# extract tab logic (pipeline, asset export, WEM extraction)
│   ├── TxtpPickerWindow.xaml# chooser when several txtp own one WEM
│   ├── WemEntry.cs          # data model
│   ├── Locale.cs            # i18n
│   ├── ConfigManager.cs     # config read/write
│   ├── Program.cs           # entry point (WPF)
│   ├── locales/             # language files (zh-CN / en-US)
│   ├── Pipeline/            # audio pipeline (migrated from the Python scripts)
│   │   ├── OdradekExporter.cs       # 0. direct game-file export + WEM audio extraction
│   │   ├── AudioPipeline.cs         # BNK extraction + index builders
│   │   ├── AudioPipeline.Mapping.cs # mapping build
│   │   ├── AudioPipeline.Export.cs  # audio export / export by ID / unused WEM
│   │   ├── WwiserRunner.cs          # invokes wwiser to generate TXTP
│   │   ├── ToolLocator.cs           # locate and download vgmstream / wwiser
│   │   ├── TxtpRepository.cs        # txtp index: WemID → owning txtp
│   │   ├── PipelinePaths.cs         # directory layout + auto-detection
│   │   └── PipelineModels.cs        # data models
│   └── README.md            # detailed WemLabeler usage guide
├── pyscript/                # original Python scripts (reference, not the main path)
├── Extracted_Banks/         # ① writes the .bnk files here; txtp/ is ②'s output; banks.xml comes from wwiser
├── WemResJson/              # 0. output: WwiseWemResource JSON
├── BankRes/                 # 0. output: WwiseBankResource JSON
├── GraphSoundRes/           # 0. output: GraphSoundResource JSON
├── GraphPgmRes/             # 0. output: GraphProgramResource JSON
├── NodeConstRes/            # 0. output: NodeConstantsResource JSON
├── WwiseID/                 # 0. output: WwiseID JSON
├── utils/                   # only holds two_repos.txt (the external tools are NOT here, see above)
├── .github/workflows/build.yml  # CI: single-file win-x64 publish
└── README_EN.md             # this file
```

`Exported_Audio/` (④'s output) and `WemResWem/` (Streaming WEM, default location) are **derived from the project root**
and therefore not in the tree above — their locations can be moved elsewhere in the path settings (e.g. onto a drive with more space).

## Output Files

### From ⓪ Export assets

| File | Description |
| --- | --- |
| `WemResJson\WwiseWemResource_*.json` | WwiseWemResource (see also "Export WEM audio") |
| `BankRes\WwiseBankResource_*.json` | WwiseBankResource |
| `GraphSoundRes\GraphSoundResource_*.json` | GraphSoundResource |
| `GraphPgmRes\GraphProgramResource_*.json` | GraphProgramResource |
| `NodeConstRes\NodeConstantsResource_*.json` | NodeConstantsResource |
| `WwiseID\WwiseID_*.json` | WwiseID |

### From ① ② ③ ④

| File | Description |
| --- | --- |
| `Extracted_Banks\*.bnk` | ① extracted Wwise Banks |
| `Extracted_Banks\txtp\*.txtp` | ② TXTP files generated by wwiser |
| `sound_wem_mapping_export.json` | ③ audio mapping table with all resource/audio-source associations |
| `missing_wem_files.csv` | ③ list of unused WEM files (complement set) |
| `streaming_wem_map.csv` | ③ record of missing Streaming WEM files |
| `mapping_build.log` | ③ detailed log of the mapping build |
| `export_progress.json` | ④ export progress, supports resuming |
| `Exported_Audio\*.wav` | ④ exported WAV files (default location) |

### From Analyze Unused WEM / Export by Event ID

| File | Description |
| --- | --- |
| `unused_wem_with_banks.csv` | Complete origin information for unused WEM files (format above) |
| `wem_map_cache.json` | WEM index cache used by "Export by Event ID" |

### From the WemLabeler labeling tab

Labels are **written back into the CSV you opened** (adding/updating the `Label`, `Duration` and `Channel` columns);
no copy with a fixed filename is produced. Use **"Export labels CSV..."** to save a copy wherever you want.

| File | Description |
| --- | --- |
| `config.json` | Tool configuration (paths, language, vgmstream path, ...), next to the exe |
| `logs\vgmstream_YYYYMMDD.log` | Startup and decode logs |

## Data Scale Reference

| Item | Size |
| --- | --- |
| `Extracted_Banks` | 74 `.bnk` files, about 1 GB |
| `Extracted_Banks\banks.xml` | 515 MB |
| `Extracted_Banks\txtp` | about 11,715 files |
| `unused_wem_with_banks.csv` | 5,951 rows |
| Raw WEM audio | about 10 GB |

## Building

```bash
dotnet build WemLabeler/WemLabeler.csproj
```

CI is in [.github/workflows/build.yml](.github/workflows/build.yml): on a push touching `WemLabeler/**` it does a
single-file win-x64 (non self-contained) publish with .NET 10 and uploads the artifact.

> **The published output must contain `Data\types.json`** (and `extensions.json`).
> `WemLabeler.csproj` already copies both from `OdradekSharp/Data/` into the output folder — don't remove that when editing the project file.

### Why `OdradekSharp/Data/types.json` is committed

It is a **byte-for-byte copy** of `odradek-game-ds2/src/main/resources/types.json` from Odradek's own repository, and it is
**not generated by any build step**: it defines every class's fields, offsets and read order, and without it not a single byte can be decoded.

So it has to be committed (`utils/` is gitignored, so a CI checkout has no other way to obtain it).
It is 8.24 MB in the working tree, but git stores it deflated at **only about 1 MB**.

## Known Limitations

- **Unported callbacks**: groups containing `PhysicsShapeResource` / `PhysicsRagdollResource` (Jolt) and
  `FacialRigSettingWithLODResource` (RigLogic) fail to read as a whole because those callbacks are not ported.
  There is a fallback now: **retry without reading subgroups**, which still exports the target objects at the cost of
  pointers inside those subgroups staying unresolved (they degrade to an unresolved `<ref>`).
- **Derived types are not exported by default**: `WwiseWemLocalizedResource` (268 objects, a derived type of
  `WwiseWemResource`) is **not exported** by default (`OdradekExporter.IncludeDerivedTypes = false`). Two reasons: it matches
  the original Odradek export, and those localization groups take several GB of RAM to read. Enabling it also requires
  changing the `WwiseWemResource_*.json` matching in the pipeline.
- **The first full export is slow**: with no existing files it reads about 2,600 groups in sequence (exactly the work the
  manual Odradek GUI export used to do). GC runs per group now, but allow some time.
- **Game updates**: types and object indices come from the game itself, so re-run the asset export after the game updates.

## Notes

- Both the asset export and "Export WEM audio" depend on a correct **game root** (containing `DS2.exe`)
- Both the `WemPath` column and "Export WEM audio" depend on a correct **Streaming WEM folder**; if that folder does not exist the app fails with an error
- Raw WEM audio is about 10 GB — check the target drive's free space first
- Exporting can take a long time; ③ and ④ both support resuming
- WemLabeler's Extract Audio tab produces output identical to the Python scripts (verified byte/row by row), so the two can be used interchangeably

## Credits

- [ShadelessFox](https://github.com/ShadelessFox) - Creator of Odradek and Decima Workshop
- [bnnm](https://github.com/bnnm) - Creator of wwiser tool
- [vgmstream team](https://github.com/vgmstream/vgmstream) - Providing game audio conversion tools
