import json
import os
import re
import subprocess

# --- 配置区 ---
WEM_RES_DIR = "./WemRes" 
TXTP_DIR = "./Extracted_Banks/txtp"
VGMSTREAM_CLI = r"E:\下载\odradek\vgmstream-r2083\vgmstream-cli.exe"  
OUTPUT_DIR = "./Decoded_Audio_Split"
CACHE_FILE = "wem_map_cache.json"

def build_wem_map(wem_res_dir, cache_file, force_refresh=False):
    """构建 WemID 索引（支持外部资源定位）"""
    if not force_refresh and os.path.exists(cache_file):
        with open(cache_file, 'r', encoding='utf-8') as f:
            temp_map = json.load(f)
            return {int(k): v for k, v in temp_map.items()}

    wem_map = {}
    file_pattern = re.compile(r'WwiseWemResource_(\d+)_(\d+)\.json')
    if not os.path.exists(wem_res_dir): return {}

    for filename in os.listdir(wem_res_dir):
        match = file_pattern.match(filename)
        if match:
            group_id, file_index = match.group(1), match.group(2)
            with open(os.path.join(wem_res_dir, filename), 'r', encoding='utf-8') as f:
                try:
                    data = json.load(f)
                    resources = data if isinstance(data, list) else [data]
                    for i, entry in enumerate(resources):
                        if isinstance(entry, dict) and entry.get("WemID"):
                            u32_id = int(entry["WemID"]) & 0xFFFFFFFF
                            wem_map[u32_id] = f"{u32_id}.wem"
                except: continue
    with open(cache_file, 'w', encoding='utf-8') as f:
        json.dump(wem_map, f, indent=2)
    return wem_map

def process_and_split_export_full(id_list, txtp_dir, wem_map, vgmstream_path, output_dir):
    """支持内外部资源自动识别并输出详细报告"""
    if not os.path.exists(output_dir): os.makedirs(output_dir)

    target_ids = {str(i) for i in id_list}
    base_dir_abs = os.path.abspath(txtp_dir)
    wem_res_abs = os.path.abspath(WEM_RES_DIR)
    
    report = {"success": [], "failed": [], "not_found_txtp": list(target_ids)}

    for filename in os.listdir(txtp_dir):
        if not filename.endswith(".txtp"): continue
        txtp_path = os.path.join(txtp_dir, filename)
        
        try:
            with open(txtp_path, 'r', encoding='utf-8') as f:
                content = f.read()
                event_match = re.search(r'CAkEvent\[\d+\]\s+(\d+)', content)
                if not event_match or event_match.group(1) not in target_ids: continue
                
                current_id = event_match.group(1)
                if current_id in report["not_found_txtp"]: report["not_found_txtp"].remove(current_id)
                
                f.seek(0)
                lines = f.readlines()
                layers = [l.strip() for l in lines if l.strip().startswith("../") or "##" in l]

                for idx, layer_line in enumerate(layers):
                    # 1. 提取 WemID (保持原样)
                    wem_id_match = re.search(r'##(\d+)\.wem', layer_line)
                    wem_name = wem_id_match.group(1) if wem_id_match else f"L{idx}"
                    u32_id = int(wem_name) if wem_name.isdigit() else None

                    # --- 关键修改点 1: 清理行首干扰项 ---
                    # 先去掉开头的问号和空格，再进行后续的 split
                    stripped_line = layer_line.lstrip('? ').strip()
                    clean_line = stripped_line.split("##fade")[0].strip()
                    
                    # 2. 定位资源路径
                    final_line = ""
                    
                    # --- 关键修改点 2: 匹配修正后的路径开头 ---
                    if clean_line.startswith("../"):
                        # 将相对路径转换为绝对路径
                        # 注意：这里直接拼接 base_dir_abs + "/../" 可能会导致路径中出现双斜杠或层级错误
                        # 建议使用 os.path.abspath 来保证路径的物理准确性
                        raw_path = clean_line.split(" #")[0].strip() # 提取路径部分
                        params = clean_line[len(raw_path):].strip() # 提取音量/偏移参数
                        
                        # 计算绝对路径：txtp 目录的父目录 + 去掉 ../ 后的路径
                        abs_base = os.path.abspath(os.path.join(base_dir_abs, ".."))
                        final_path = os.path.normpath(os.path.join(abs_base, raw_path.replace("../", "", 1)))
                        
                        final_line = f"{final_path} {params}".strip()

                    elif u32_id and u32_id in wem_map:
                        target_path = os.path.join(wem_res_abs, f"{u32_id}.wem")
                        if os.path.exists(target_path):
                            # 对外部 wem，重新拼接参数
                            params = clean_line.split(".wem")[-1]
                            final_line = f"{target_path} {params}".strip()
                        else:
                            report["failed"].append(f"Event {current_id} -> Wem {u32_id} (文件在 WemRes 中缺失)")
                            continue
                    else:
                        # 如果走到这里，说明这一行既不是以 ../ 开头，也不在 wem_map 里
                        # 很大概率是因为 clean_line 依然带着 ? 或者其他格式错误
                        report["failed"].append(f"Event {current_id} -> 层 {idx} (无法识别: {clean_line[:30]}...)")
                        continue

                    # --- 后续创建 temp_txtp 和调用 vgmstream 的逻辑 ---

                    # 3. 导出处理
                    temp_txtp = os.path.join(output_dir, f"temp_{current_id}_{idx}.txtp")
                    with open(temp_txtp, 'w', encoding='utf-8') as tf: tf.write(final_line)
                    
                    output_wav = os.path.join(output_dir, f"{current_id}_{wem_name}.wav")
                    cmd = [vgmstream_path, "-o", output_wav, temp_txtp]
                    
                    try:
                        print(f"正在导出: {output_wav}")
                        subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
                        report["success"].append(f"{current_id}_{wem_name}")
                    except subprocess.CalledProcessError as e:
                        report["failed"].append(f"{current_id}_{wem_name} (vgmstream 报错)")
                    
                    if os.path.exists(temp_txtp): os.remove(temp_txtp)

        except Exception as e: print(f"解析 {filename} 失败: {e}")

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
current_wem_map = build_wem_map(WEM_RES_DIR, CACHE_FILE)
TARGET_IDS = [2609648901] 
process_and_split_export_full(TARGET_IDS, TXTP_DIR, current_wem_map, VGMSTREAM_CLI, OUTPUT_DIR)