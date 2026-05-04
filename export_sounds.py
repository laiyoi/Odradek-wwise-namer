import json
import os
import re
import subprocess

# --- 配置区 ---
BASE_DIR = r"D:\Odradek-wwise-namer"
GRAPH_SOUND_RES_DIR = os.path.join(BASE_DIR, "GraphSoundRes")
GRAPH_PGM_RES_DIR = os.path.join(BASE_DIR, "GraphPgmRes")
NODE_CONST_RES_DIR = os.path.join(BASE_DIR, "NodeConstRes")
WWISE_ID_DIR = os.path.join(BASE_DIR, "WwiseID")
TXTP_DIR = os.path.join(BASE_DIR, "Extracted_Banks", "txtp")
# 从指定路径读取已重命名的wem文件（文件名为wemid，可能有负数需要转换）
RENAMED_WEM_DIR = os.path.join(BASE_DIR, "WemNamer", "RENAMED")
OUTPUT_DIR = r"G:\ds2 unpack\wems\Exported_Audio"
VGMSTREAM_CLI = r"E:\下载\odradek\vgmstream-r2083\vgmstream-cli.exe"
PROGRESS_FILE = os.path.join(BASE_DIR, "export_progress.json")
STREAMING_CSV = os.path.join(BASE_DIR, "streaming_wem_map.csv")
MAPPING_JSON = os.path.join(BASE_DIR, "sound_wem_mapping_export.json")

# CSV写入锁（防止多线程冲突，虽然当前是单线程）
csv_lock = False


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
    if os.path.exists(PROGRESS_FILE):
        try:
            with open(PROGRESS_FILE, 'r', encoding='utf-8') as f:
                data = json.load(f)
                return set(tuple(item) for item in data)
        except Exception as e:
            print(f"加载进度失败: {e}")
    return set()


def build_txtp_index():
    txtp_index = {}
    if not os.path.exists(TXTP_DIR):
        return txtp_index
    event_pattern = re.compile(r'CAkEvent\[(\d+)\]\s+(\d+)')
    for filename in os.listdir(TXTP_DIR):
        if not filename.endswith('.txtp'):
            continue
        filepath = os.path.join(TXTP_DIR, filename)
        try:
            with open(filepath, 'r', encoding='utf-8') as f:
                content = f.read()
                match = event_pattern.search(content)
                if match:
                    event_id = int(match.group(2)) & 0xFFFFFFFF
                    txtp_index[event_id] = filepath
        except Exception:
            continue
    return txtp_index


def build_renamed_wem_index():
    """构建重命名wem文件索引，处理u32负数文件名"""
    wem_index = {}
    if not os.path.exists(RENAMED_WEM_DIR):
        return wem_index
    for filename in os.listdir(RENAMED_WEM_DIR):
        if not filename.endswith('.wem'):
            continue
        name_part = filename[:-4]
        try:
            file_id = int(name_part)
            u32_id = file_id & 0xFFFFFFFF
            filepath = os.path.join(RENAMED_WEM_DIR, filename)
            wem_index[u32_id] = filepath
        except ValueError:
            continue
    return wem_index


def sanitize_filename(name):
    return re.sub(r'[\\/*?:"<>|]', '_', name)


def export_audio(temp_txtp_content, output_wav, vgmstream_path):
    temp_txtp_path = os.path.join(OUTPUT_DIR, "_temp.txtp")
    try:
        with open(temp_txtp_path, 'w', encoding='utf-8') as f:
            f.write(temp_txtp_content)
        cmd = [vgmstream_path, "-o", output_wav, temp_txtp_path]
        result = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        return result.returncode == 0
    except Exception:
        return False
    finally:
        if os.path.exists(temp_txtp_path):
            os.remove(temp_txtp_path)


def export_renamed_wem(wem_path, output_wav, vgmstream_path):
    """使用vgmstream导出重命名的wem文件"""
    try:
        cmd = [vgmstream_path, "-o", output_wav, wem_path]
        result = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        return result.returncode == 0
    except Exception:
        return False


def build_existing_files_set():
    """从输出目录构建已存在文件的集合"""
    existing = set()
    if not os.path.exists(OUTPUT_DIR):
        return existing
    for filename in os.listdir(OUTPUT_DIR):
        if filename.endswith('.wav'):
            base_name = filename[:-4]
            if '_' in base_name:
                parts = base_name.rsplit('_', 1)
                if parts[-1].isdigit():
                    base_name = parts[0]
            existing.add(base_name)
    return existing


def process_all_sounds():
    txtp_index = build_txtp_index()
    renamed_wem_index = build_renamed_wem_index()

    if not os.path.exists(OUTPUT_DIR):
        os.makedirs(OUTPUT_DIR)

    report = {"success": [], "failed": [], "skipped": [], "resumed": 0, "streaming": []}
    mapping_data = []
    
    # 用于批量写入CSV的缓冲区
    csv_buffer = []
    CSV_BUFFER_SIZE = 10  # 每10条写入一次

    base_dir_abs = os.path.abspath(TXTP_DIR)
    abs_base = os.path.abspath(os.path.join(base_dir_abs, ".."))

    processed = load_progress()
    existing_files = build_existing_files_set()
    
    # 如果有进度文件或已存在文件，说明是恢复模式，不清空CSV
    # 否则清空CSV重新开始
    if not processed and not existing_files:
        clear_csv()
        print("[*] 清空CSV文件，重新开始记录")

    resumed_count = len(processed)
    if existing_files:
        resumed_count += len(existing_files)

    report["resumed"] = resumed_count

    pattern = re.compile(r'GraphSoundResource_(\d+)_(\d+)\.json')
    all_files = sorted([f for f in os.listdir(GRAPH_SOUND_RES_DIR) if f.endswith('.json') and pattern.match(f)])
    total_files = len(all_files)
    processed_count = 0

    print(f"开始处理 {total_files} 个文件 | TXTP: {len(txtp_index)} | WEM: {len(renamed_wem_index)} | 已存在: {len(existing_files)} | 已处理: {len(processed)}")

    for filename in all_files:
        match = pattern.match(filename)
        if not match:
            continue

        sound_path = os.path.join(GRAPH_SOUND_RES_DIR, filename)
        sound_data = load_json(sound_path)
        if not sound_data:
            continue

        resource_name = sound_data.get("ResourceName", "Unknown")
        graph_program_ref = sound_data.get("GraphProgram", "")

        ref = parse_ref(graph_program_ref)
        if not ref:
            continue

        pgm_group, pgm_index = ref
        pgm_filename = f"GraphProgramResource_{pgm_group}_{pgm_index}.json"
        pgm_path = os.path.join(GRAPH_PGM_RES_DIR, pgm_filename)
        pgm_data = load_json(pgm_path)

        if not pgm_data:
            continue

        exposed_data_ref = pgm_data.get("ExposedDataResource", "")
        exposed_ref = parse_ref(exposed_data_ref)

        if not exposed_ref:
            continue

        nc_group, nc_index = exposed_ref
        nc_filename = f"NodeConstantsResource_{nc_group}_{nc_index}.json"
        nc_path = os.path.join(NODE_CONST_RES_DIR, nc_filename)
        nc_data = load_json(nc_path)

        if not nc_data or not isinstance(nc_data, dict):
            continue

        params = nc_data.get("Parameters", {})
        soft_linked = params.get("DefaultSoftLinkedObjects", [])

        for soft_ref_str in soft_linked:
            soft_ref = parse_ref(soft_ref_str)
            if not soft_ref:
                continue
            wwise_group, wwise_index = soft_ref

            wwise_filename = f"WwiseID_{wwise_group}_{wwise_index}.json"
            wwise_path = os.path.join(WWISE_ID_DIR, wwise_filename)
            wwise_data = load_json(wwise_path)

            if not wwise_data or not isinstance(wwise_data, dict) or wwise_data.get("Id") is None:
                continue

            wwise_id = int(wwise_data["Id"]) & 0xFFFFFFFF

            key = (resource_name, wwise_id)
            if key in processed:
                continue

            clean_name = sanitize_filename(resource_name)
            if clean_name in existing_files:
                report["skipped"].append(f"{resource_name} (输出文件已存在)")
                continue

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
                report["skipped"].append(f"{resource_name} -> WwiseID {wwise_id} (txtp未找到)")
                current_mapping["TXTP_Filename"] = "NOT_FOUND"
                current_mapping["AudioSources"].append({
                    "WemID": None,
                    "SourceType": "TXTP_NotFound",
                    "BankFile": None,
                    "WemRes_Coord": None,
                    "RawLine": None
                })
                mapping_data.append(current_mapping)
                processed.add(key)
                save_progress(processed)
                continue

            txtp_path = txtp_index[wwise_id]
            txtp_filename = os.path.basename(txtp_path)
            current_mapping["TXTP_Filename"] = txtp_filename

            try:
                with open(txtp_path, 'r', encoding='utf-8') as f:
                    lines = f.readlines()
            except Exception:
                continue

            wem_pattern = re.compile(r'##\d+\.wem')
            layers = []
            for l in lines:
                stripped = l.strip()
                if stripped.startswith("../") or stripped.startswith("wem/") or wem_pattern.search(stripped):
                    if not stripped.startswith("group =") and not stripped.startswith(".plugin-"):
                        layers.append(stripped)

            if not layers:
                report["skipped"].append(f"{resource_name} -> WwiseID {wwise_id} (txtp无音频层)")
                current_mapping["AudioSources"].append({
                    "WemID": None,
                    "SourceType": "NoAudioLayers",
                    "BankFile": None,
                    "WemRes_Coord": None,
                    "RawLine": None
                })
                mapping_data.append(current_mapping)
                processed.add(key)
                save_progress(processed)
                continue

            # 收集该资源的所有导出结果用于一行日志
            export_results = []

            for idx, layer_line in enumerate(layers):
                wem_id_match = re.search(r'##(\d+)\.wem', layer_line)
                if not wem_id_match:
                    wem_id_match = re.search(r'wem/(\d+)\.wem', layer_line)
                wem_name = wem_id_match.group(1) if wem_id_match else f"L{idx}"
                u32_id = int(wem_name) if wem_name.isdigit() else None

                stripped_line = layer_line.lstrip('? ').strip()
                clean_line = stripped_line.split("##fade")[0].strip()

                final_line = ""
                source_type = "Unknown"

                source_info = {
                    "WemID": u32_id,
                    "SourceType": "Unknown",
                    "BankFile": None,
                    "WemRes_Coord": None,
                    "RawLine": clean_line
                }

                if clean_line.startswith("../"):
                    raw_path = clean_line.split(" #")[0].strip()
                    params = clean_line[len(raw_path):].strip()
                    final_path = os.path.normpath(os.path.join(abs_base, raw_path.replace("../", "", 1)))
                    final_line = f"{final_path} {params}".strip()
                    source_type = "Embedded"
                    source_info["SourceType"] = "Embedded"
                    source_info["BankFile"] = os.path.basename(raw_path)
                elif clean_line.startswith("wem/"):
                    if u32_id and u32_id in renamed_wem_index:
                        wem_path = renamed_wem_index[u32_id]
                        source_type = "Streaming"
                        source_info["SourceType"] = "Streaming"
                        source_info["WemRes_Coord"] = f"WemID:{u32_id}"

                        base_name = sanitize_filename(resource_name)
                        if len(layers) > 1:
                            output_name = f"{base_name}_{idx:02d}.wav"
                        else:
                            output_name = f"{base_name}.wav"
                        output_path = os.path.join(OUTPUT_DIR, output_name)

                        if os.path.exists(output_path):
                            report["skipped"].append(f"{output_name} (文件已存在)")
                            source_info["ExportStatus"] = "Skipped_Exists"
                            current_mapping["AudioSources"].append(source_info)
                            export_results.append(f"S{idx}:已存在")
                            continue

                        success = export_renamed_wem(wem_path, output_path, VGMSTREAM_CLI)

                        if success:
                            report["success"].append(output_name)
                            source_info["ExportStatus"] = "Success"
                            export_results.append(f"S{idx}:OK")
                        else:
                            report["failed"].append(f"{output_name} (vgmstream导出失败)")
                            source_info["ExportStatus"] = "Failed"
                            export_results.append(f"S{idx}:失败")

                        current_mapping["AudioSources"].append(source_info)
                    else:
                        # 文件未找到，记录到CSV
                        csv_line = f"{u32_id},WwiseWemResource,{resource_name}"
                        csv_buffer.append(csv_line)
                        report["streaming"].append(csv_line)
                        
                        # 批量写入CSV
                        if len(csv_buffer) >= CSV_BUFFER_SIZE:
                            append_to_csv(csv_buffer)
                            csv_buffer = []
                        
                        report["failed"].append(f"{resource_name} -> Wem {u32_id} (Streaming文件未找到)")
                        source_info["SourceType"] = "Streaming_NotFound"
                        source_info["WemRes_Coord"] = f"WemID:{u32_id}"
                        current_mapping["AudioSources"].append(source_info)
                        export_results.append(f"S{idx}:无文件")
                    continue
                elif u32_id:
                    report["failed"].append(f"{resource_name} -> Wem {u32_id} (未知模式)")
                    source_info["SourceType"] = "UnknownFormat"
                    current_mapping["AudioSources"].append(source_info)
                    export_results.append(f"L{idx}:未知格式")
                    continue
                else:
                    report["failed"].append(f"{resource_name} -> 层 {idx} (无法识别)")
                    source_info["SourceType"] = "Unrecognized"
                    source_info["WemID"] = None
                    current_mapping["AudioSources"].append(source_info)
                    export_results.append(f"L{idx}:无法识别")
                    continue

                base_name = sanitize_filename(resource_name)

                if len(layers) > 1:
                    output_name = f"{base_name}_{idx:02d}.wav"
                else:
                    output_name = f"{base_name}.wav"

                output_path = os.path.join(OUTPUT_DIR, output_name)

                if os.path.exists(output_path):
                    report["skipped"].append(f"{output_name} (文件已存在)")
                    source_info["ExportStatus"] = "Skipped_Exists"
                    current_mapping["AudioSources"].append(source_info)
                    export_results.append(f"E{idx}:已存在")
                    continue

                success = export_audio(final_line, output_path, VGMSTREAM_CLI)

                if success:
                    report["success"].append(output_name)
                    source_info["ExportStatus"] = "Success"
                    export_results.append(f"E{idx}:OK")
                else:
                    report["failed"].append(f"{output_name} (vgmstream失败)")
                    source_info["ExportStatus"] = "Failed"
                    export_results.append(f"E{idx}:失败")

                current_mapping["AudioSources"].append(source_info)

            # 将当前mapping条目添加到总数据中
            mapping_data.append(current_mapping)

            processed.add(key)
            processed_count += 1

            # 每处理10个项目保存一次进度和mapping
            if processed_count % 10 == 0:
                save_progress(processed)
                save_mapping_json(mapping_data)

            # 一行日志输出该资源的所有处理结果
            layers_str = ", ".join(export_results) if export_results else "无音频层"
            print(f"[{processed_count}/{total_files}] {resource_name} | {layers_str}")

        # 保存进度和mapping
        save_progress(processed)
        save_mapping_json(mapping_data)

    # 确保最后剩余的CSV缓冲区被写入
    if csv_buffer:
        append_to_csv(csv_buffer)
        csv_buffer = []

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
    
    # 简单的锁机制防止并发写入
    while csv_lock:
        import time
        time.sleep(0.01)
    
    csv_lock = True
    try:
        # 检查文件是否存在，不存在则写入表头
        write_header = not os.path.exists(STREAMING_CSV)
        
        with open(STREAMING_CSV, 'a', encoding='utf-8') as f:
            if write_header:
                f.write("ObjectId,Type,name\n")
            for line in csv_lines:
                f.write(line + "\n")
                f.flush()  # 立即刷新到磁盘
    except Exception as e:
        print(f"追加CSV失败: {e}")
    finally:
        csv_lock = False


def clear_csv():
    """清理CSV文件（用于重新开始时）"""
    if os.path.exists(STREAMING_CSV):
        try:
            os.remove(STREAMING_CSV)
        except Exception as e:
            print(f"清理CSV失败: {e}")


def clear_progress():
    """清理进度文件（正常完成时调用）"""
    if os.path.exists(PROGRESS_FILE):
        try:
            os.remove(PROGRESS_FILE)
            print("进度文件已清理")
        except Exception as e:
            print(f"清理进度文件失败: {e}")


if __name__ == "__main__":
    print("开始导出音频...")
    report, mapping_data = process_all_sounds()

    # 正常完成时清理进度文件
    clear_progress()

    # 最终保存mapping JSON
    save_mapping_json(mapping_data)
    print(f"Mapping JSON已保存到: {MAPPING_JSON}")

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
