import json
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

# --- 配置区 ---
BASE_DIR = Path(r"D:\Odradek-wwise-namer")
GRAPH_SOUND_RES_DIR = BASE_DIR / "GraphSoundRes"
GRAPH_PGM_RES_DIR = BASE_DIR / "GraphPgmRes"
NODE_CONST_RES_DIR = BASE_DIR / "NodeConstRes"
WWISE_ID_DIR = BASE_DIR / "WwiseID"
TXTP_DIR = BASE_DIR / "Extracted_Banks" / "txtp"
BANKS_XML = BASE_DIR / "Extracted_Banks" / "banks.xml"
# 从指定路径读取已重命名的wem文件（文件名为wemid，可能有负数需要转换）
RENAMED_WEM_DIR = Path(r"G:\ds2 unpack\wems\RENAMED")
OUTPUT_DIR = Path(r"G:\ds2 unpack\wems\Exported_Audio")
VGMSTREAM_CLI = Path(r"E:\下载\odradek\vgmstream-r2083\vgmstream-cli.exe")
PROGRESS_FILE = BASE_DIR / "export_progress.json"
STREAMING_CSV = BASE_DIR / "streaming_wem_map.csv"
MISSING_WEM_CSV = BASE_DIR / "missing_wem_files.csv"
MAPPING_JSON = BASE_DIR / "sound_wem_mapping_export.json"

# CSV写入锁（防止多线程冲突，虽然当前是单线程）
csv_lock = False

# 日志文件路径
MAPPING_LOG_FILE = BASE_DIR / "mapping_build.log"


def parse_ref(ref_str):
    match = re.search(r'<ref to\s+(\d+):(\d+)>', ref_str)
    if match:
        return int(match.group(1)), int(match.group(2))
    return None


def load_json(filepath):
    try:
        with open(filepath, 'r', encoding='utf-8') as f:
            return json.load(f)
    except Exception:
        return None


def save_progress(processed):
    """保存进度到文件"""
    try:
        with open(PROGRESS_FILE, 'w', encoding='utf-8') as f:
            json.dump(list(processed), f)
    except Exception as e:
        print(f"保存进度失败: {e}")


def load_progress():
    """从文件加载进度"""
    if PROGRESS_FILE.exists():
        try:
            with open(PROGRESS_FILE, 'r', encoding='utf-8') as f:
                data = json.load(f)
                return set(tuple(item) for item in data)
        except Exception as e:
            print(f"加载进度失败: {e}")
    return set()


def build_txtp_index():
    txtp_index = {}
    if not TXTP_DIR.exists():
        return txtp_index
    event_pattern = re.compile(r'CAkEvent\[(\d+)\]\s+(\d+)')
    for filepath in TXTP_DIR.iterdir():
        if not filepath.is_file() or filepath.suffix != '.txtp':
            continue
        try:
            with open(filepath, 'r', encoding='utf-8') as f:
                content = f.read()
                match = event_pattern.search(content)
                if match:
                    event_id = int(match.group(2)) & 0xFFFFFFFF
                    txtp_index[event_id] = str(filepath)
        except Exception:
            continue
    return txtp_index


def build_renamed_wem_index():
    """构建重命名wem文件索引，处理u32负数文件名"""
    wem_index = {}
    if not RENAMED_WEM_DIR.exists():
        return wem_index
    for filepath in RENAMED_WEM_DIR.iterdir():
        if not filepath.is_file() or filepath.suffix != '.wem':
            continue
        try:
            file_id = int(filepath.stem)
            u32_id = file_id & 0xFFFFFFFF
            wem_index[u32_id] = str(filepath)
        except ValueError:
            continue
    return wem_index


def build_bank_media_index():
    """从banks.xml构建media索引，记录每个media在哪个bank中"""
    media_index = {}
    if not BANKS_XML.exists():
        return media_index
    
    try:
        tree = ET.parse(BANKS_XML)
        root = tree.getroot()
        
        # 遍历所有bank
        for bank in root.findall(".//root"):
            bank_filename = bank.get("filename")
            if not bank_filename:
                continue
            
            # 遍历所有MediaHeader
            for media_header in bank.findall(".//obj[@na='MediaHeader']"):
                # 找sid字段
                sid_field = media_header.find(".//fld[@na='id']")
                if sid_field is None:
                    continue
                
                sid_value = sid_field.get("value") or sid_field.get("va")
                if sid_value is None:
                    continue
                
                try:
                    media_id = int(sid_value)
                    # 转换为u32
                    media_id_u32 = media_id & 0xFFFFFFFF
                    # 记录在media_index中
                    if media_id_u32 not in media_index:
                        media_index[media_id_u32] = []
                    media_index[media_id_u32].append(bank_filename)
                except ValueError:
                    continue
    except Exception as e:
        print(f"加载banks.xml失败: {e}")
    
    return media_index


def sanitize_filename(name):
    return re.sub(r'[\\/*?:"<>|]', '_', name)


def export_audio(temp_txtp_content, output_wav, vgmstream_path):
    temp_txtp_path = OUTPUT_DIR / "_temp.txtp"
    try:
        with open(temp_txtp_path, 'w', encoding='utf-8') as f:
            f.write(temp_txtp_content)
        cmd = [str(vgmstream_path), "-o", str(output_wav), str(temp_txtp_path)]
        result = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        return result.returncode == 0
    except Exception:
        return False
    finally:
        if temp_txtp_path.exists():
            temp_txtp_path.unlink()


def export_renamed_wem(wem_path, output_wav, vgmstream_path):
    """使用vgmstream导出重命名的wem文件"""
    try:
        cmd = [str(vgmstream_path), "-o", str(output_wav), str(wem_path)]
        result = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        return result.returncode == 0
    except Exception:
        return False


def build_existing_files_set():
    """从输出目录构建已存在文件的集合"""
    existing = set()
    if not OUTPUT_DIR.exists():
        return existing
    for filepath in OUTPUT_DIR.iterdir():
        if filepath.is_file() and filepath.suffix == '.wav':
            base_name = filepath.stem
            if '_' in base_name:
                parts = base_name.rsplit('_', 1)
                if parts[-1].isdigit():
                    base_name = parts[0]
            existing.add(base_name)
    return existing


def log_to_file(message):
    """记录日志到文件"""
    try:
        with open(MAPPING_LOG_FILE, 'a', encoding='utf-8') as f:
            f.write(message + "\n")
    except Exception:
        pass


def append_to_missing_wem_csv(csv_lines):
    """动态追加缺失WEM记录到CSV文件（被引用但找不到的WEM）"""
    global csv_lock
    if not csv_lines:
        return

    while csv_lock:
        import time
        time.sleep(0.01)

    csv_lock = True
    try:
        write_header = not MISSING_WEM_CSV.exists()
        with open(MISSING_WEM_CSV, 'a', encoding='utf-8') as f:
            if write_header:
                f.write("WemID,ResourceName,Reason\n")
            for line in csv_lines:
                f.write(line + "\n")
                f.flush()
    except Exception as e:
        print(f"追加缺失WEM CSV失败: {e}")
    finally:
        csv_lock = False


def clear_missing_wem_csv():
    """清理缺失WEM CSV文件"""
    if MISSING_WEM_CSV.exists():
        try:
            MISSING_WEM_CSV.unlink()
        except Exception as e:
            print(f"清理缺失WEM CSV失败: {e}")


def write_unused_wem_csv(used_wem_ids):
    """写入未被使用的WEM文件列表（补集）"""
    renamed_wem_index = build_renamed_wem_index()
    unused_wems = []
    
    for wem_id in renamed_wem_index:
        if wem_id not in used_wem_ids:
            wem_path = Path(renamed_wem_index[wem_id])
            unused_wems.append({
                "WemID": wem_id,
                "Filename": wem_path.name,
                "Path": str(wem_path)
            })
    
    if unused_wems:
        try:
            with open(MISSING_WEM_CSV, 'w', encoding='utf-8') as f:
                f.write("WemID,Filename,Path\n")
                for wem in unused_wems:
                    f.write(f"{wem['WemID']},{wem['Filename']},{wem['Path']}\n")
            print(f"[*] 未使用的WEM记录: {MISSING_WEM_CSV} ({len(unused_wems)}个)")
        except Exception as e:
            print(f"写入未使用WEM CSV失败: {e}")
    else:
        print(f"[*] 所有WEM文件都被使用了")


def build_mapping():
    """阶段一：构建完整的mapping数据，不导出音频"""
    print("="*60)
    print("[阶段一] 构建音频映射表...")
    print("="*60)

    # 清空旧的日志文件和CSV
    if MAPPING_LOG_FILE.exists():
        MAPPING_LOG_FILE.unlink()
    clear_missing_wem_csv()

    start_time = time.time()
    txtp_index = build_txtp_index()
    renamed_wem_index = build_renamed_wem_index()
    bank_media_index = build_bank_media_index()

    mapping_data = []
    skipped_count = 0
    error_count = 0
    missing_wem_buffer = []
    MISSING_WEM_BUFFER_SIZE = 10
    
    # 收集所有被使用的WEM ID（用于计算补集）
    used_wem_ids = set()

    base_dir_abs = TXTP_DIR.resolve()
    abs_base = base_dir_abs.parent.resolve()

    pattern = re.compile(r'GraphSoundResource_(\d+)_(\d+)\.json')
    all_files = sorted([f for f in GRAPH_SOUND_RES_DIR.iterdir()
                        if f.is_file() and f.suffix == '.json' and pattern.match(f.name)])
    total_files = len(all_files)

    print(f"[*] 扫描到 {total_files} 个 GraphSoundResource 文件")
    print(f"[*] TXTP索引: {len(txtp_index)} 个 | WEM索引: {len(renamed_wem_index)} 个 | Bank索引: {len(bank_media_index)} 个")
    print("-"*60)

    for i, filepath in enumerate(all_files, 1):
        match = pattern.match(filepath.name)
        if not match:
            continue

        sound_data = load_json(filepath)
        if not sound_data:
            error_count += 1
            continue

        resource_name = sound_data.get("ResourceName", "Unknown")
        graph_program_ref = sound_data.get("GraphProgram", "")

        ref = parse_ref(graph_program_ref)
        if not ref:
            error_count += 1
            continue

        pgm_group, pgm_index = ref
        pgm_filename = f"GraphProgramResource_{pgm_group}_{pgm_index}.json"
        pgm_path = GRAPH_PGM_RES_DIR / pgm_filename
        pgm_data = load_json(pgm_path)

        if not pgm_data:
            error_count += 1
            continue

        exposed_data_ref = pgm_data.get("ExposedDataResource", "")
        exposed_ref = parse_ref(exposed_data_ref)

        if not exposed_ref:
            error_count += 1
            continue

        nc_group, nc_index = exposed_ref
        nc_filename = f"NodeConstantsResource_{nc_group}_{nc_index}.json"
        nc_path = NODE_CONST_RES_DIR / nc_filename
        nc_data = load_json(nc_path)

        if not nc_data or not isinstance(nc_data, dict):
            error_count += 1
            continue

        params = nc_data.get("Parameters", {})
        soft_linked = params.get("DefaultSoftLinkedObjects", [])

        # 收集该文件的所有处理结果用于一行日志
        file_results = []
        # 收集错误信息用于写入日志文件
        log_entries = []

        for soft_ref_str in soft_linked:
            soft_ref = parse_ref(soft_ref_str)
            if not soft_ref:
                continue
            wwise_group, wwise_index = soft_ref

            wwise_filename = f"WwiseID_{wwise_group}_{wwise_index}.json"
            wwise_path = WWISE_ID_DIR / wwise_filename
            wwise_data = load_json(wwise_path)

            if not wwise_data or not isinstance(wwise_data, dict) or wwise_data.get("Id") is None:
                error_count += 1
                continue

            wwise_id = int(wwise_data["Id"]) & 0xFFFFFFFF

            current_mapping = {
                "ResourceName": resource_name,
                "GraphProgram": f"{pgm_group}:{pgm_index}",
                "ExposedDataResource": f"{nc_group}:{nc_index}",
                "WwiseID_Value": wwise_id,
                "WwiseID_Coord": f"{wwise_group}:{wwise_index}",
                "TXTP_Filename": None,
                "AudioSources": []
            }

            if wwise_id not in txtp_index:
                skipped_count += 1
                current_mapping["TXTP_Filename"] = "NOT_FOUND"
                
                # 尝试在banks.xml中查找
                bank_info = ""
                if wwise_id in bank_media_index:
                    banks = bank_media_index[wwise_id]
                    bank_info = f" (Banks: {', '.join(banks)})"
                    # 为每个bank添加一个AudioSource
                    for bank_filename in banks:
                        current_mapping["AudioSources"].append({
                            "WemID": wwise_id,
                            "SourceType": "TXTP_NotFound_BankKnown",
                            "BankFile": bank_filename,
                            "WemRes_Coord": None,
                            "RawLine": None
                        })
                    # 添加到file_results
                    bank_names = [Path(bank).name for bank in banks]
                    file_results.append(f"无TXTP[{', '.join(bank_names)}]")
                else:
                    current_mapping["AudioSources"].append({
                        "WemID": wwise_id,
                        "SourceType": "TXTP_NotFound_BankUnknown",
                        "BankFile": None,
                        "WemRes_Coord": None,
                        "RawLine": None
                    })
                    file_results.append("无TXTP")
                    
                mapping_data.append(current_mapping)
                # 记录到日志文件，不在控制台显示
                log_entries.append(f"TXTP未找到: {resource_name} (WwiseID: {wwise_id}){bank_info}")
                continue

            txtp_path = txtp_index[wwise_id]
            txtp_filename = Path(txtp_path).name
            current_mapping["TXTP_Filename"] = txtp_filename

            try:
                with open(txtp_path, 'r', encoding='utf-8') as f:
                    lines = f.readlines()
            except Exception:
                error_count += 1
                continue

            wem_pattern = re.compile(r'##\d+\.wem')
            layers = []
            for l in lines:
                stripped = l.strip()
                if stripped.startswith("../") or stripped.startswith("wem/") or wem_pattern.search(stripped):
                    if not stripped.startswith("group =") and not stripped.startswith(".plugin-"):
                        layers.append(stripped)

            if not layers:
                skipped_count += 1
                current_mapping["AudioSources"].append({
                    "WemID": None,
                    "SourceType": "NoAudioLayers",
                    "BankFile": None,
                    "WemRes_Coord": None,
                    "RawLine": None
                })
                mapping_data.append(current_mapping)
                # 记录到日志文件，不在控制台显示
                log_entries.append(f"无音频层: {resource_name} (WwiseID: {wwise_id})")
                continue

            layer_results = []
            has_error = False
            for idx, layer_line in enumerate(layers):
                wem_id_match = re.search(r'##(\d+)\.wem', layer_line)
                if not wem_id_match:
                    wem_id_match = re.search(r'wem/(\d+)\.wem', layer_line)
                wem_name = wem_id_match.group(1) if wem_id_match else f"L{idx}"
                u32_id = int(wem_name) if wem_name.isdigit() else None

                stripped_line = layer_line.lstrip('? ').strip()
                clean_line = stripped_line.split("##fade")[0].strip()

                source_info = {
                    "WemID": u32_id,
                    "SourceType": "Unknown",
                    "BankFile": None,
                    "WemRes_Coord": None,
                    "RawLine": clean_line
                }

                if clean_line.startswith("../"):
                    raw_path = clean_line.split(" #")[0].strip()
                    source_info["SourceType"] = "Embedded"
                    source_info["BankFile"] = Path(raw_path).name
                    layer_results.append(f"E{idx}")
                    # 记录被使用的WEM ID
                    if u32_id:
                        used_wem_ids.add(u32_id)
                elif clean_line.startswith("wem/"):
                    if u32_id and u32_id in renamed_wem_index:
                        source_info["SourceType"] = "Streaming"
                        source_info["WemRes_Coord"] = f"WemID:{u32_id}"
                        layer_results.append(f"S{idx}")
                        # 记录被使用的WEM ID
                        used_wem_ids.add(u32_id)
                    else:
                        source_info["SourceType"] = "Streaming_NotFound"
                        source_info["WemRes_Coord"] = f"WemID:{u32_id}"
                        has_error = True
                        layer_results.append(f"S{idx}(缺失)")
                        # 记录到日志文件
                        log_entries.append(f"Streaming文件缺失: {resource_name} (WemID: {u32_id})")
                        # 记录到缺失WEM CSV
                        csv_line = f"{u32_id},{resource_name},Streaming文件缺失"
                        missing_wem_buffer.append(csv_line)
                        if len(missing_wem_buffer) >= MISSING_WEM_BUFFER_SIZE:
                            append_to_missing_wem_csv(missing_wem_buffer)
                            missing_wem_buffer = []
                elif u32_id:
                    source_info["SourceType"] = "UnknownFormat"
                    has_error = True
                    layer_results.append(f"L{idx}(未知)")
                    log_entries.append(f"未知格式: {resource_name} (层 {idx})")
                    # 记录被使用的WEM ID
                    used_wem_ids.add(u32_id)
                else:
                    source_info["SourceType"] = "Unrecognized"
                    source_info["WemID"] = None
                    has_error = True
                    layer_results.append(f"L{idx}(错误)")
                    log_entries.append(f"无法识别: {resource_name} (层 {idx})")

                current_mapping["AudioSources"].append(source_info)

            mapping_data.append(current_mapping)
            # 简化控制台输出：只显示层数和是否有错误
            if has_error:
                file_results.append(f"{len(layers)}层[{' '.join(layer_results)}]")
            else:
                file_results.append(f"{len(layers)}层")

        # 一行日志输出该文件的所有处理结果
        if file_results:
            results_str = " | ".join(file_results)
            print(f"[{i}/{total_files}] {resource_name} | {results_str}")

        # 将错误信息写入日志文件
        for log_entry in log_entries:
            log_to_file(log_entry)

    # 保存最后剩余的缺失WEM记录
    if missing_wem_buffer:
        append_to_missing_wem_csv(missing_wem_buffer)
        missing_wem_buffer = []

    # 输出未被使用的WEM文件列表（补集）
    write_unused_wem_csv(used_wem_ids)
    
    # 保存mapping
    save_mapping_json(mapping_data)
    elapsed = time.time() - start_time

    print("="*60)
    print(f"[完成] Mapping构建完成!")
    print(f"[*] 总资源: {len(mapping_data)}")
    print(f"[*] 跳过: {skipped_count} | 错误: {error_count}")
    print(f"[*] 被使用的WEM数量: {len(used_wem_ids)}")
    print(f"[*] 耗时: {elapsed:.1f}秒")
    print(f"[*] 已保存到: {MAPPING_JSON}")
    if MAPPING_LOG_FILE.exists():
        print(f"[*] 详细日志: {MAPPING_LOG_FILE}")
    print("="*60)

    return mapping_data


def export_from_mapping():
    """阶段二：基于已构建的mapping导出音频"""
    print("="*60)
    print("[阶段二] 基于Mapping导出音频...")
    print("="*60)

    start_time = time.time()

    if not MAPPING_JSON.exists():
        print("[!] 找不到mapping文件，请先运行阶段一构建mapping")
        return None, None

    mapping_data = load_json(MAPPING_JSON)
    if not mapping_data:
        print("[!] Mapping文件为空或损坏")
        return None, None

    renamed_wem_index = build_renamed_wem_index()

    if not OUTPUT_DIR.exists():
        OUTPUT_DIR.mkdir(parents=True)

    report = {"success": [], "failed": [], "skipped": [], "resumed": 0, "streaming": []}
    csv_buffer = []
    CSV_BUFFER_SIZE = 10

    base_dir_abs = TXTP_DIR.resolve()
    abs_base = base_dir_abs.parent.resolve()

    processed = load_progress()
    existing_files = build_existing_files_set()

    if not processed and not existing_files:
        clear_csv()
        print("[*] 清空CSV文件，重新开始记录")

    report["resumed"] = len(processed)
    if existing_files:
        report["resumed"] += len(existing_files)

    total = len(mapping_data)
    processed_count = 0

    print(f"[*] 加载了 {total} 个mapping条目")
    print(f"[*] WEM索引: {len(renamed_wem_index)} 个 | 已存在: {len(existing_files)} | 已处理: {len(processed)}")
    print("-"*60)

    for i, item in enumerate(mapping_data, 1):
        resource_name = item["ResourceName"]
        wwise_id = item["WwiseID_Value"]
        key = (resource_name, wwise_id)

        if key in processed:
            continue

        clean_name = sanitize_filename(resource_name)
        if clean_name in existing_files:
            report["skipped"].append(f"{resource_name} (输出文件已存在)")
            continue

        # 检查是否需要导出（有实际音频源且不是错误状态）
        audio_sources = item.get("AudioSources", [])
        if not audio_sources or audio_sources[0].get("SourceType") in ["TXTP_NotFound", "NoAudioLayers"]:
            report["skipped"].append(f"{resource_name} (无有效音频源)")
            processed.add(key)
            continue

        export_results = []

        for idx, source_info in enumerate(audio_sources):
            source_type = source_info.get("SourceType", "Unknown")
            u32_id = source_info.get("WemID")
            raw_line = source_info.get("RawLine", "")

            if source_type == "Embedded":
                # 构建导出路径和参数
                raw_path = raw_line.split(" #")[0].strip()
                params = raw_line[len(raw_path):].strip()
                final_path = (abs_base / raw_path.replace("../", "", 1)).resolve()
                final_line = f"{final_path} {params}".strip()

                base_name = sanitize_filename(resource_name)
                if len(audio_sources) > 1:
                    output_name = f"{base_name}_{idx:02d}.wav"
                else:
                    output_name = f"{base_name}.wav"
                output_path = OUTPUT_DIR / output_name

                if output_path.exists():
                    report["skipped"].append(f"{output_name} (文件已存在)")
                    export_results.append(f"E{idx}:已存在")
                    continue

                success = export_audio(final_line, output_path, VGMSTREAM_CLI)
                if success:
                    report["success"].append(output_name)
                    export_results.append(f"E{idx}:OK")
                else:
                    report["failed"].append(f"{output_name} (vgmstream失败)")
                    export_results.append(f"E{idx}:失败")

            elif source_type == "Streaming":
                if u32_id and u32_id in renamed_wem_index:
                    wem_path = renamed_wem_index[u32_id]

                    base_name = sanitize_filename(resource_name)
                    if len(audio_sources) > 1:
                        output_name = f"{base_name}_{idx:02d}.wav"
                    else:
                        output_name = f"{base_name}.wav"
                    output_path = OUTPUT_DIR / output_name

                    if output_path.exists():
                        report["skipped"].append(f"{output_name} (文件已存在)")
                        export_results.append(f"S{idx}:已存在")
                        continue

                    success = export_renamed_wem(wem_path, output_path, VGMSTREAM_CLI)
                    if success:
                        report["success"].append(output_name)
                        export_results.append(f"S{idx}:OK")
                    else:
                        report["failed"].append(f"{output_name} (vgmstream导出失败)")
                        export_results.append(f"S{idx}:失败")
                else:
                    csv_line = f"{u32_id},WwiseWemResource,{resource_name}"
                    csv_buffer.append(csv_line)
                    report["streaming"].append(csv_line)
                    if len(csv_buffer) >= CSV_BUFFER_SIZE:
                        append_to_csv(csv_buffer)
                        csv_buffer = []
                    report["failed"].append(f"{resource_name} -> Wem {u32_id} (Streaming文件未找到)")
                    export_results.append(f"S{idx}:无文件")

            elif source_type in ["Streaming_NotFound", "UnknownFormat", "Unrecognized"]:
                if source_type == "Streaming_NotFound" and u32_id:
                    csv_line = f"{u32_id},WwiseWemResource,{resource_name}"
                    csv_buffer.append(csv_line)
                    report["streaming"].append(csv_line)
                    if len(csv_buffer) >= CSV_BUFFER_SIZE:
                        append_to_csv(csv_buffer)
                        csv_buffer = []
                export_results.append(f"{idx}:跳过")

        processed.add(key)
        processed_count += 1

        if processed_count % 10 == 0:
            save_progress(processed)

        # 一行日志输出该资源的所有处理结果
        if export_results:
            results_str = ", ".join(export_results)
            print(f"[{processed_count}/{total}] {resource_name} | {results_str}")

    # 确保最后剩余的CSV缓冲区被写入
    if csv_buffer:
        append_to_csv(csv_buffer)

    save_progress(processed)
    elapsed = time.time() - start_time

    print("="*60)
    print(f"[完成] 音频导出完成!")
    print(f"[*] 成功: {len(report['success'])} | 失败: {len(report['failed'])} | 跳过: {len(report['skipped'])}")
    print(f"[*] 耗时: {elapsed:.1f}秒")
    print("="*60)

    return report, mapping_data


def save_mapping_json(mapping_data):
    """保存mapping数据到JSON文件"""
    try:
        with open(MAPPING_JSON, 'w', encoding='utf-8') as f:
            json.dump(mapping_data, f, indent=2, ensure_ascii=False)
    except Exception as e:
        print(f"保存mapping JSON失败: {e}")


def append_to_csv(csv_lines):
    """动态追加CSV行到文件"""
    global csv_lock
    if not csv_lines:
        return

    while csv_lock:
        import time
        time.sleep(0.01)

    csv_lock = True
    try:
        write_header = not STREAMING_CSV.exists()
        with open(STREAMING_CSV, 'a', encoding='utf-8') as f:
            if write_header:
                f.write("ObjectId,Type,name\n")
            for line in csv_lines:
                f.write(line + "\n")
                f.flush()
    except Exception as e:
        print(f"追加CSV失败: {e}")
    finally:
        csv_lock = False


def clear_csv():
    """清理CSV文件（用于重新开始时）"""
    if STREAMING_CSV.exists():
        try:
            STREAMING_CSV.unlink()
        except Exception as e:
            print(f"清理CSV失败: {e}")


def clear_progress():
    """清理进度文件（正常完成时调用）"""
    if PROGRESS_FILE.exists():
        try:
            PROGRESS_FILE.unlink()
            print("进度文件已清理")
        except Exception as e:
            print(f"清理进度文件失败: {e}")


def run_export_flow(mode):
    """执行导出流程"""
    if mode == "1":
        # 阶段一：只构建mapping
        build_mapping()
    elif mode == "2":
        # 阶段二：基于mapping导出
        if not MAPPING_JSON.exists():
            print("[!] 未找到mapping文件，请先运行阶段一")
            return

        report, mapping_data = export_from_mapping()

        if report:
            # 正常完成时清理进度文件
            clear_progress()

            print("\n" + "="*60)
            print(f"导出统计")
            print(f" - 已跳过(上次处理): {report['resumed']}")
            print(f" - 成功数量: {len(report['success'])}")
            print(f" - 失败数量: {len(report['failed'])}")
            print(f" - 跳过数量: {len(report['skipped'])}")
            print(f" - Streaming记录: {len(report['streaming'])}")

            if report["streaming"]:
                print(f"Streaming CSV已动态写入: {STREAMING_CSV}")

            if report["failed"]:
                print("\n失败列表:")
                for f in report["failed"][:20]:
                    print(f"   [!] {f}")
                if len(report["failed"]) > 20:
                    print(f"   ... 还有 {len(report['failed']) - 20} 个失败")

            print("="*60)
    elif mode == "12":
        # 先构建mapping，再导出
        build_mapping()
        print()
        report, mapping_data = export_from_mapping()

        if report:
            # 正常完成时清理进度文件
            clear_progress()

            print("\n" + "="*60)
            print(f"导出统计")
            print(f" - 已跳过(上次处理): {report['resumed']}")
            print(f" - 成功数量: {len(report['success'])}")
            print(f" - 失败数量: {len(report['failed'])}")
            print(f" - 跳过数量: {len(report['skipped'])}")
            print(f" - Streaming记录: {len(report['streaming'])}")

            if report["streaming"]:
                print(f"Streaming CSV已动态写入: {STREAMING_CSV}")

            if report["failed"]:
                print("\n失败列表:")
                for f in report["failed"][:20]:
                    print(f"   [!] {f}")
                if len(report["failed"]) > 20:
                    print(f"   ... 还有 {len(report['failed']) - 20} 个失败")

            print("="*60)
    else:
        print(f"[!] 未知模式: {mode}")


if __name__ == "__main__":
    # 命令行参数处理
    if len(sys.argv) > 1:
        mode = sys.argv[1]
        run_export_flow(mode)
    else:
        # 交互式模式
        print("="*60)
        print("音频导出工具")
        print("="*60)
        print()
        print("请选择模式:")
        print("  1 = 阶段一：构建mapping")
        print("  2 = 阶段二：基于mapping导出音频")
        print("  12 = 先构建mapping，再导出音频")
        print("  q = 退出")
        print()

        while True:
            try:
                user_input = input("请输入选项: ").strip().lower()

                if user_input in ("q", "quit", "exit"):
                    print("退出程序")
                    break
                elif user_input in ("1", "2", "12"):
                    run_export_flow(user_input)
                    print()
                else:
                    print(f"[!] 无效选项: {user_input}")
                    print("请选择: 1, 2, 12 或 q")
            except KeyboardInterrupt:
                print("\n退出程序")
                break
            except EOFError:
                print("\n退出程序")
                break
