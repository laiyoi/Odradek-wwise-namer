import json
import os
import csv

def fnv1_32_hash(string):
    """标准的 Wwise FNV-1 32位哈希计算"""
    string = string.lower().strip()
    h = 2166136261
    for char in string:
        h = (h * 16777619) & 0xFFFFFFFF
        h = (h ^ ord(char)) & 0xFFFFFFFF
    return h

def extract_ref_id(ref_str):
    """从 '<ref to 31123:5787>' 字符串中提取 '31123_5787'"""
    if not isinstance(ref_str, str) or ":" not in ref_str:
        return None
    return ref_str.replace("<ref to ", "").replace(">", "").replace(":", "_")

def build_ds2_audio_manifest(json_folder):
    # 建立多级映射表
    # 1. 逻辑名称 -> 关联的图表 (Graph ID)
    event_to_graph = {} 
    # 2. 图表 (Graph ID) -> 物理 Wem 资源的引用 ID
    graph_to_wem_ref = {}
    # 3. 物理 Wem 资源引用 ID -> 最终的物理坐标数据
    wem_ref_to_data = {}

    print(f"[*] 正在分析目录: {json_folder} ...")

    # 遍历所有文件建立关联链[cite: 1, 3]
    for root, _, files in os.walk(json_folder):
        for file in files:
            if not file.endswith(".json"): continue
            path = os.path.join(root, file)
            file_ref_id = file.split('_', 1)[1].replace('.json', '') if '_' in file else None
            
            try:
                with open(path, 'r', encoding='utf-8') as f:
                    data = json.load(f)

                # A. 处理音频入口：提取 ResourceName 和引用的 Graph
                if "ResourceName" in data and "GraphProgram" in data:
                    res_name = data["ResourceName"]
                    graph_id = extract_ref_id(data["GraphProgram"])
                    if graph_id:
                        event_to_graph[res_name] = graph_id

                # B. 处理逻辑图表：提取引用的 Wem 资源 ID
                # 检查 ExposedDataResource 或其它 Hard/Soft 链接
                if "ExposedDataResource" in data:
                    wem_ref = extract_ref_id(data["ExposedDataResource"])
                    if wem_ref and file_ref_id:
                        graph_to_wem_ref[file_ref_id] = wem_ref

                # C. 处理物理终端：记录 WemID 和物理坐标
                if "WemID" in data and "StreamingDataSource" in data:
                    src = data["StreamingDataSource"]
                    if file_ref_id:
                        wem_ref_to_data[file_ref_id] = {
                            "WemID": data["WemID"],
                            "Locator": src.get("Locator"),
                            "Offset": src.get("Offset"),
                            "Length": src.get("Length"),
                            "Duration": data.get("mLengthInSeconds", 0)
                        }
            except Exception:
                continue

    # 汇总结果并生成 CSV
    output_file = "DS2_Audio_Master_Index.csv"
    count = 0
    with open(output_file, 'w', newline='', encoding='utf-8') as csvfile:
        fieldnames = ['ResourceName', 'WwiseID_Calc', 'WemID', 'Locator', 'Offset', 'Length', 'Duration_Sec']
        writer = csv.DictWriter(csvfile, fieldnames=fieldnames)
        writer.writeheader()

        for res_name, graph_id in event_to_graph.items():
            # 顺着逻辑链查找物理数据：Event -> Graph -> WemResource
            wem_ref = graph_to_wem_ref.get(graph_id)
            if wem_ref and wem_ref in wem_ref_to_data:
                p_data = wem_ref_to_data[wem_ref]
                writer.writerow({
                    'ResourceName': res_name,
                    'WwiseID_Calc': fnv1_32_hash(res_name),
                    'WemID': p_data['WemID'],
                    'Locator': p_data['Locator'],
                    'Offset': p_data['Offset'],
                    'Length': p_data['Length'],
                    'Duration_Sec': p_data['Duration']
                })
                count += 1

    print(f"\n[OK] 索引创建完成！")
    print(f"[*] 成功关联了 {count} 条音频资源至物理坐标。")
    print(f"[*] 结果已保存至: {output_file}")

# --- 执行配置 ---
# 请将此路径修改为你存放解包后 JSON 的总目录
target_directory = "./" 
if os.path.exists(target_directory):
    build_ds2_audio_manifest(target_directory)
else:
    print(f"[!] 找不到目录: {target_directory}")