# Odradek-wwise-namer

<p align="center">
  <a href="README.md">中文版</a>
</p>

An audio file naming and export tool based on [Odradek](https://github.com/ShadelessFox/odradek), designed for **Death Stranding 2**.

## Introduction

[Odradek](https://github.com/ShadelessFox/odradek) is a Horizon Forbidden West asset viewer and extractor, a reincarnation of [Decima Workshop](https://github.com/ShadelessFox/decima). It is designed for modders working with Decima engine games, providing capabilities for viewing and extracting game assets.

This project utilizes Odradek as a foundation to automate the naming and exporting of Death Stranding 2 audio resources.

## Prerequisites

- [Odradek](https://github.com/ShadelessFox/odradek) - For exporting game assets
- [wwiser](https://github.com/bnnm/wwiser) - For parsing Wwise Banks and generating TXTP files
- [vgmstream](https://github.com/vgmstream/vgmstream) - For converting WEM to WAV
- Python 3.x
- PowerShell

## Usage Steps

### Step 1: Export Resources with Odradek

Use Odradek to export the following resources from Death Stranding 2 (all in JSON format):

| Resource Type | Export Format | Export Path |
|---------|---------|---------|
| WwiseWemResource | `.wem` files | `d:\Odradek-wwise-namer\WemNamer\WEM` |
| WwiseWemResource | `.json` files | `d:\Odradek-wwise-namer\WemRes` |
| WwiseBankResource | `.json` files | `d:\Odradek-wwise-namer\BankRes` |
| GraphSoundResource | `.json` files | `d:\Odradek-wwise-namer\GraphSoundRes` |
| GraphProgramResource | `.json` files | `d:\Odradek-wwise-namer\GraphPgmRes` |
| NodeConstantsResource | `.json` files | `d:\Odradek-wwise-namer\NodeConstRes` |
| WwiseID | `.json` files | `d:\Odradek-wwise-namer\WwiseID` |

### Step 2: Run Renaming and Extraction Scripts

#### 2.1 Rename WEM Files

Run the PowerShell script to rename WEM files based on WemID from JSON:

```powershell
cd d:\Odradek-wwise-namer\WemNamer
.\jsonrename.ps1
```

This script will:
- Read `.wem` files from `WemNamer\WEM`
- Rename files based on `WemID` from corresponding JSON in `WemRes`
- Move renamed files to `WemNamer\RENAMED` directory

#### 2.2 Extract BNK Files

Run the Python script to extract Wwise Bank data from JSON:

```bash
cd d:\Odradek-wwise-namer
python extract_bnk_from_json.py
```

This script will:
- Read JSON files from `BankRes`
- Decode Base64-encoded BankData
- Fix Wwise Bank data alignment issues
- Save extracted `.bnk` files to `Extracted_Banks` directory

### Step 3: Generate TXTP with wwiser

Use [wwiser](https://github.com/bnnm/wwiser) to read all `.bnk` files in the `Extracted_Banks` directory and generate `.txtp` files.

1. Open wwiser (double-click `wwiser.pyz`)
2. Click **Load dirs...**, select `d:\Odradek-wwise-namer\Extracted_Banks` directory
3. Click **Generate TXTP** to generate `.txtp` files
4. Ensure `.txtp` files are generated in `Extracted_Banks\txtp` directory

### Step 4: Run Export Script

```bash
cd d:\Odradek-wwise-namer
python export_sounds.py
```

This script will:
- Build an audio mapping table, associating GraphSoundResource, GraphProgramResource, NodeConstantsResource, and WwiseID
- Parse audio sources (Embedded or Streaming) from TXTP files
- Use vgmstream to export audio as WAV format
- Generate `streaming_wem_map.csv` to record missing Streaming WEM files
- Generate `missing_wem_files.csv` to record unused WEM files (complement set)

### Step 5: Analyze Unused WEM Files (Optional)

Run the `link_unused_wem.py` script to analyze the origin of unused WEM files:

```bash
cd d:\Odradek-wwise-namer
python link_unused_wem.py
```

This script will:
- Read `missing_wem_files.csv` (list of unused WEM files)
- Parse `banks.xml` to find which banks contain these WEM files
- Parse `WwiseID` directory to find corresponding WwiseID
- Parse `WemRes` directory to find original WwiseWemResource files
- Generate `unused_wem_with_banks.csv` with complete association information

**Output Format**:

| Field | Description |
|-------|-------------|
| WemID | ID of the WEM file |
| FoundInWemRes | Whether found in WemRes |
| WemResCoord | WemRes coordinate (e.g., `2:878`) |
| FoundInBanks | Whether found in banks.xml |
| Banks | List of bank files containing this WEM |
| BankIDs | dwSoundBankID of the banks |
| FoundInWwiseID | Whether found in WwiseID |
| WwiseIDCoord | WwiseID coordinate |

#### Script Configuration

In `export_sounds.py`, you can modify the following configurations:

```python
BASE_DIR = Path(r"D:\Odradek-wwise-namer")          # Project root directory
OUTPUT_DIR = Path(r"G:\ds2 unpack\wems\Exported_Audio")  # Export directory
VGMSTREAM_CLI = Path(r"E:\下载\odradek\vgmstream-r2083\vgmstream-cli.exe")  # vgmstream path
```

#### Command Line Arguments

The script supports command line arguments to control processing mode:

```bash
# Phase 1: Build mapping only
python export_sounds.py 1

# Phase 2: Export audio based on existing mapping
python export_sounds.py 2

# Build mapping then export audio (one-click complete)
python export_sounds.py 12
```

When run without arguments, the script enters **interactive mode** and prompts you to select an operation.

#### Two-Phase Processing

The script uses a two-phase processing architecture for better efficiency:

- **Phase 1 (Build Mapping)**: Parse all JSON and TXTP files, generate `sound_wem_mapping_export.json`
  - Read-only, very fast
  - Generate detailed mapping table with all audio source information
  - Record missing WEM files to `missing_wem_files.csv`

- **Phase 2 (Export)**: Export audio directly based on the mapping table
  - Skip repetitive JSON parsing
  - Focus on audio export with resume support
  - Automatically record export progress to `export_progress.json`

## Project Structure

```
Odradek-wwise-namer/
├── WemNamer/
│   ├── WEM/              # Original WEM files (exported from Odradek)
│   ├── RENAMED/          # Renamed WEM files
│   └── jsonrename.ps1    # WEM renaming script
├── WemRes/               # WwiseWemResource JSON files
├── BankRes/              # WwiseBankResource JSON files
├── Extracted_Banks/      # Extracted BNK files
│   ├── txtp/             # TXTP files generated by wwiser
│   └── banks.xml         # Bank information generated by wwiser
├── GraphSoundRes/        # GraphSoundResource JSON files
├── GraphPgmRes/          # GraphProgramResource JSON files
├── NodeConstRes/         # NodeConstantsResource JSON files
├── WwiseID/              # WwiseID JSON files
├── extract_bnk_from_json.py  # BNK extraction script
├── export_sounds.py      # Main audio export script
├── export_by_id.py       # Export specific audio by ID
├── build_audio_manifest.py   # Build audio resource manifest
├── fix_negative_ids.py   # Fix negative IDs in JSON
├── link_unused_wem.py    # Analyze origin of unused WEM files
└── README_EN.md          # This file
```

## Output Files

After running the script, the following files will be generated:

### From `export_sounds.py`
| File | Description |
|------|-------------|
| `sound_wem_mapping_export.json` | Audio mapping table with all resource and audio source associations |
| `export_progress.json` | Export progress for resume support |
| `streaming_wem_map.csv` | Missing Streaming WEM files record |
| `missing_wem_files.csv` | **List of unused WEM files** (complement set) |
| `mapping_build.log` | Detailed log of mapping build process |

### From `link_unused_wem.py`
| File | Description |
|------|-------------|
| `unused_wem_with_banks.csv` | Complete origin information for unused WEM files, including WemRes coordinates, bank information, and WwiseID associations |

## Notes

- Ensure all JSON resource files are correctly exported, otherwise scripts may fail to find corresponding references
- vgmstream path needs to be modified according to your actual setup
- Export process may take a long time, script supports resuming from interruption (via `export_progress.json`)
- Streaming type WEM files need to exist in `WemNamer\RENAMED` to be correctly exported
- If some WEM files are missing, check `missing_wem_files.csv` for details

## Credits

- [ShadelessFox](https://github.com/ShadelessFox) - Creator of Odradek and Decima Workshop
- [bnnm](https://github.com/bnnm) - Creator of wwiser tool
- [vgmstream team](https://github.com/vgmstream/vgmstream) - Providing game audio conversion tools
