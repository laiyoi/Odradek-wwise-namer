import json
import os

def fix_overflow(val, bits=32):
    """将有符号负数转换为无符号正数"""
    if isinstance(val, int) and val < 0:
        return val + (1 << bits)
    return val

def process_and_save_jsons(folder_path):
    print(f"[*] 开始处理目录: {folder_path}")
    count = 0

    for root, dirs, files in os.walk(folder_path):
        for file in files:
            if file.endswith(".json"):
                file_path = os.path.join(root, file)
                try:
                    with open(file_path, 'r', encoding='utf-8') as f:
                        data = json.load(f)

                    # 标记是否发生修改
                    modified = [False]

                    def recursive_fix(obj):
                        if isinstance(obj, dict):
                            for k, v in obj.items():
                                # 针对 32 位 ID
                                if k in ["WemID", "WwiseID", "ResourceNameHash", "WemSize", "Length"]:
                                    new_v = fix_overflow(v, 32)
                                    if new_v != v:
                                        obj[k] = new_v
                                        modified[0] = True
                                # 针对 64 位 Locator (Decima 引擎中 Locator 通常是 64 位)
                                elif k in ["Locator"]:
                                    new_v = fix_overflow(v, 64)
                                    if new_v != v:
                                        obj[k] = new_v
                                        modified[0] = True
                                else:
                                    recursive_fix(v)
                        elif isinstance(obj, list):
                            for item in obj:
                                recursive_fix(item)

                    recursive_fix(data)

                    # 如果数据被修改，覆盖保存
                    if modified[0]:
                        with open(file_path, 'w', encoding='utf-8') as f:
                            json.dump(data, f, indent=2, ensure_ascii=False)
                        count += 1
                        if count % 100 == 0:
                            print(f"[+] 已修正并保存 {count} 个文件...")

                except Exception as e:
                    print(f"[!] 错误文件 {file}: {e}")

    print(f"\n[OK] 修复完成！共更新了 {count} 个包含负数 ID 的 JSON 文件。")

# --- 使用说明 ---
# 将下面的路径替换为你存放 JSON 的文件夹路径
json_root_directory = "./" 
process_and_save_jsons(json_root_directory)