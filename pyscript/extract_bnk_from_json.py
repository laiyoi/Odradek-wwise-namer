import os
import json
import base64
import struct

# --- 配置区域 ---
input_folder = "./BankRes"          # JSON 文件夹
output_folder = "./Extracted_Banks"    # 输出文件夹
# ----------------

if not os.path.exists(output_folder):
    os.makedirs(output_folder)

def fix_wwise_data(raw_data):
    """
    针对 Wwise 银行文件的特殊修复逻辑：
    1. 自动补齐因截断缺失的字节。
    2. 定位最后一个 Chunk，根据其声明长度进行精确截断，移除脏数据。
    """
    # 常见的 Wwise Chunk 标签
    tags = [b'BKHD', b'HIRC', b'DIDX', b'DATA', b'STID', b'ENVS', b'PLAT', b'INIT']
    
    last_tag_pos = -1
    for tag in tags:
        pos = raw_data.rfind(tag)
        if pos > last_tag_pos:
            last_tag_pos = pos
            
    if last_tag_pos == -1:
        return raw_data # 没找到标签，返回原数据

    try:
        # 获取最后一个块声明的数据长度 (Tag 4字节 + Length 4字节)
        declared_len = struct.unpack("<I", raw_data[last_tag_pos+4:last_tag_pos+8])[0]
        # 理论上的文件终点 = 块起始位置 + Tag(4) + Length(4) + 数据长度
        theoretical_end = last_tag_pos + 8 + declared_len
        
        actual_len = len(raw_data)
        
        if actual_len < theoretical_end:
            # 情况 A: 数据被截断了，补齐缺失的字节（通常是 \0）
            padding_needed = theoretical_end - actual_len
            return raw_data + (b'\x00' * padding_needed)
        elif actual_len > theoretical_end:
            # 情况 B: 有多余的填充字节（导致 wwiser 报错 0x00000000），精确截断
            return raw_data[:theoretical_end]
    except:
        pass
        
    return raw_data

def smart_decode(b64_str):
    # 补齐 Base64 自身的填充符号
    missing_padding = len(b64_str) % 4
    if missing_padding:
        b64_str += "=" * (4 - missing_padding)
    
    try:
        binary_data = base64.b64decode(b64_str)
        # 执行 Wwise 块对齐修复
        return fix_wwise_data(binary_data)
    except Exception as e:
        print(f"Base64 解码失败: {e}")
        return None

print(f"正在启动自动化提取与精准对齐修复程序...")

count = 0
# 按文件名排序处理
for filename in sorted(os.listdir(input_folder)):
    if filename.endswith(".json"):
        filepath = os.path.join(input_folder, filename)
        with open(filepath, 'r', encoding='utf-8') as f:
            try:
                data = json.load(f)
                raw_str = data.get("BankData") or data.get("Data", {}).get("BankData")
                
                if raw_str:
                    binary_data = smart_decode(raw_str)
                    
                    if binary_data and binary_data.startswith(b'BKHD'):
                        output_filename = filename.replace(".json", ".bnk")
                        output_path = os.path.join(output_folder, output_filename)
                        with open(output_path, "wb") as bnk_file:
                            bnk_file.write(binary_data)
                        count += 1
                        # print(f"[完成] {output_filename}")
            except Exception as e:
                print(f"[错误] 处理 {filename}: {e}")

print(f"\n处理完成！共成功修复并提取 {count} 个 .bnk 文件。")
print(f"提示：如果仍有文件报错，请确认该 Bank 在游戏中是否包含有效内容。")