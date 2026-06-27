import json
import os
import re
import subprocess
from pathlib import Path

# --- 配置区 ---
WEM_RES_DIR = Path("./WemResJson")
WEM_RES_WEM_DIR = Path("G:\ds2_unpack\wems\WemResWem")
TXTP_DIR = Path("./Extracted_Banks/txtp")
VGMSTREAM_CLI = r"E:\下载\odradek\vgmstream-r2083\vgmstream-cli.exe"  
OUTPUT_DIR = Path("./Decoded_Audio_Split")
CACHE_FILE = "wem_map_cache.json"

def build_wem_map(wem_res_dir, wem_res_wem_dir, cache_file, force_refresh=False):
    """构建 WemID -> WemResWem文件路径 索引"""
    cache_path = Path(cache_file)
    if not force_refresh and cache_path.exists():
        with open(cache_path, 'r', encoding='utf-8') as f:
            temp_map = json.load(f)
            return {int(k): v for k, v in temp_map.items()}

    wem_map = {}
    file_pattern = re.compile(r'WwiseWemResource_(\d+)_(\d+)\.json')
    if not wem_res_dir.exists():
        return wem_map

    for filename in os.listdir(str(wem_res_dir)):
        match = file_pattern.match(filename)
        if not match:
            continue
        group_id, file_index = match.group(1), match.group(2)
        with open(str(wem_res_dir / filename), 'r', encoding='utf-8') as f:
            try:
                data = json.load(f)
                resources = data if isinstance(data, list) else [data]
                for entry in resources:
                    if isinstance(entry, dict) and entry.get("WemID"):
                        u32_id = int(entry["WemID"]) & 0xFFFFFFFF
                        wem_file = wem_res_wem_dir / f"WwiseWemResource_{group_id}_{file_index}.wem"
                        if wem_file.exists():
                            wem_map[u32_id] = str(wem_file)
            except Exception:
                continue

    with open(cache_path, 'w', encoding='utf-8') as f:
        json.dump(wem_map, f, indent=2)
    return wem_map

def process_and_split_export_full(id_list, txtp_dir, wem_map, vgmstream_path, output_dir):
    """支持内外部资源自动识别并输出详细报告"""
    if not output_dir.exists():
        output_dir.mkdir(parents=True)

    target_ids = {str(i) for i in id_list}
    
    report = {"success": [], "failed": [], "not_found_txtp": list(target_ids)}

    for txtp_file in txtp_dir.iterdir():
        if not txtp_file.is_file() or txtp_file.suffix != '.txtp':
            continue
        txtp_path = str(txtp_file)
        
        try:
            with open(txtp_path, 'r', encoding='utf-8') as f:
                content = f.read()
                event_match = re.search(r'CAkEvent\[\d+\]\s+(\d+)', content)
                if not event_match or event_match.group(1) not in target_ids: continue
                
                current_id = event_match.group(1)
                if current_id in report["not_found_txtp"]: report["not_found_txtp"].remove(current_id)
                
                f.seek(0)
                lines = f.readlines()
                layers = [l.strip() for l in lines if l.strip().startswith("../") or "##" in l or l.strip().startswith("wem/")]

                for idx, layer_line in enumerate(layers):
                    # 1. 提取 WemID (保持原样)
                    wem_id_match = re.search(r'##(\d+)\.wem', layer_line)
                    if not wem_id_match:
                        wem_id_match = re.search(r'wem/(\d+)\.wem', layer_line)
                    wem_name = wem_id_match.group(1) if wem_id_match else f"L{idx}"
                    u32_id = int(wem_name) if wem_name.isdigit() else None

                    stripped_line = layer_line.lstrip('? ').strip()
                    clean_line = stripped_line.split("##fade")[0].strip()
                    
                    final_line = ""
                    
                    if clean_line.startswith("../"):
                        raw_path = clean_line.split(" #")[0].strip()
                        params = clean_line[len(raw_path):].strip()
                        
                        abs_base = txtp_dir.resolve().parent
                        final_path = (abs_base / raw_path.replace("../", "", 1)).resolve()
                        
                        final_line = f"{final_path} {params}".strip()

                    elif u32_id and u32_id in wem_map:
                        target_path = wem_map[u32_id]
                        if target_path and os.path.exists(target_path):
                            params = clean_line.split(".wem")[-1]
                            final_line = f"{target_path} {params}".strip()
                        else:
                            report["failed"].append(f"Event {current_id} -> Wem {u32_id} (文件在 WemResWem 中缺失)")
                            continue
                    else:
                        report["failed"].append(f"Event {current_id} -> 层 {idx} (无法识别: {clean_line[:30]}...)")
                        continue

                    # 3. 导出处理
                    temp_txtp = output_dir / f"temp_{current_id}_{idx}.txtp"
                    with open(temp_txtp, 'w', encoding='utf-8') as tf:
                        tf.write(final_line)
                    
                    output_wav = output_dir / f"{current_id}_{wem_name}.wav"
                    cmd = [vgmstream_path, "-o", str(output_wav), str(temp_txtp)]
                    
                    try:
                        print(f"正在导出: {output_wav}")
                        subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
                        report["success"].append(f"{current_id}_{wem_name}")
                    except subprocess.CalledProcessError as e:
                        report["failed"].append(f"{current_id}_{wem_name} (vgmstream 报错)")
                    
                    if temp_txtp.exists():
                        temp_txtp.unlink()

        except Exception as e:
            print(f"解析 {txtp_file.name} 失败: {e}")

    # --- 输出最终报告 ---
    print("\n" + "="*50)
    print(f"导出统计 [目标 ID: {', '.join(target_ids)}]")
    print(f" - 成功数量: {len(report['success'])}")
    print(f" - 失败数量: {len(report['failed'])}")
    for f in report["failed"]: print(f"   [!] {f}")
    if report["not_found_txtp"]:
        print(f" - 未找到定义: {', '.join(report['not_found_txtp'])}")
    print("="*50)

# --- 运行 ---
current_wem_map = build_wem_map(WEM_RES_DIR, WEM_RES_WEM_DIR, CACHE_FILE)
TARGET_IDS = [2609648901] 
process_and_split_export_full(TARGET_IDS, TXTP_DIR, current_wem_map, VGMSTREAM_CLI, OUTPUT_DIR)