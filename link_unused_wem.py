#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
从WemResJson构建所有WEM的索引，交叉引用BankRes(WemIDs)、WemResWem和txtp
"""

import csv
import json
import re
from pathlib import Path


BASE_DIR = Path(r"E:\Odradek-wwise-namer")
BANK_RES_DIR = BASE_DIR / "BankRes"
TXTP_DIR = BASE_DIR / "Extracted_Banks" / "txtp"
WEM_RES_JSON_DIR = BASE_DIR / "WemResJson"
WEM_RES_WEM_DIR = Path(r"G:\ds2 unpack\wems\WemResWem")
OUTPUT_CSV = BASE_DIR / "unused_wem_with_banks.csv"
MAPPING_EXPORT_JSON = BASE_DIR / "sound_wem_mapping_export.json"


def build_wem_res_json_index():
    """从WemResJson构建 WemID -> {坐标, IsStreaming} 索引"""
    index = {}
    if not WEM_RES_JSON_DIR.exists():
        print(f"[!] 找不到WemResJson目录: {WEM_RES_JSON_DIR}")
        return index

    for json_file in WEM_RES_JSON_DIR.glob("WwiseWemResource_*.json"):
        try:
            parts = json_file.stem.split('_')
            if len(parts) < 3:
                continue
            with open(json_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
            if 'WemID' not in data:
                continue
            wem_id = int(data['WemID']) & 0xFFFFFFFF
            index[wem_id] = {
                'Coord': f"{parts[1]}:{parts[2]}",
                'JsonFile': json_file.name,
                'IsStreaming': data.get('IsStreaming', ''),
            }
        except Exception:
            continue
    return index


def build_wem_res_wem_index():
    """从WemResWem扫描实际存在的.wem文件 -> {coord: filename}"""
    index = {}
    wem_pattern = re.compile(r'WwiseWemResource_(\d+)_(\d+)\.wem')
    if not WEM_RES_WEM_DIR.exists():
        return index
    for wem_file in WEM_RES_WEM_DIR.iterdir():
        if not wem_file.is_file() or wem_file.suffix != '.wem':
            continue
        match = wem_pattern.match(wem_file.name)
        if not match:
            continue
        coord = f"{match.group(1)}:{match.group(2)}"
        index[coord] = wem_file.name
    return index


def build_bank_res_wem_ids():
    """从BankRes JSON的WemIDs字段构建所有被bank引用的WemID集合"""
    wem_ids_set = set()
    if not BANK_RES_DIR.exists():
        print(f"[!] 找不到BankRes目录: {BANK_RES_DIR}")
        return wem_ids_set

    for json_file in sorted(BANK_RES_DIR.glob("WwiseBankResource_*.json")):
        try:
            with open(json_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
            ids = data.get('WemIDs', [])
            if ids:
                # 确保每个ID是32位无符号整数
                wem_ids_set.update(int(i) & 0xFFFFFFFF for i in ids)
        except Exception as e:
            print(f"[!] 解析 {json_file.name} 失败: {e}")
            continue
    return wem_ids_set


def build_txtp_wem_index():
    """从txtp文件扫描所有被引用的WemID -> {wem_id: [txtp文件名, ...]}"""
    wem_index = {}
    if not TXTP_DIR.exists():
        print(f"[!] 找不到txtp目录: {TXTP_DIR}")
        return wem_index

    pattern = re.compile(r'(?:##|wem/)(\d+)\.wem')
    for txtp_file in TXTP_DIR.glob("*.txtp"):
        if not txtp_file.is_file():
            continue
        try:
            with open(txtp_file, 'r', encoding='utf-8') as f:
                content = f.read()
            for match in pattern.finditer(content):
                wem_id = int(match.group(1)) & 0xFFFFFFFF
                if wem_id not in wem_index:
                    wem_index[wem_id] = []
                wem_index[wem_id].append(txtp_file.name)
        except Exception:
            continue
    return wem_index


def build_used_wem_ids():
    """从sound_wem_mapping_export.json收集已使用的WemID（已在Mapping中导出的）"""
    used_ids = set()
    if not MAPPING_EXPORT_JSON.exists():
        print(f"[!] 找不到 {MAPPING_EXPORT_JSON}")
        return used_ids
    try:
        with open(MAPPING_EXPORT_JSON, 'r', encoding='utf-8') as f:
            data = json.load(f)
        for entry in data:
            for src in entry.get('AudioSources', []):
                wem_id = src.get('WemID')
                if wem_id is not None:
                    used_ids.add(int(wem_id) & 0xFFFFFFFF)
    except Exception as e:
        print(f"[!] 解析 {MAPPING_EXPORT_JSON} 失败: {e}")
    return used_ids


def main():
    print("=" * 60)
    print("从WemResJson构建WEM索引，交叉引用BankRes(WemIDs)、txtp和WemResWem")
    print("=" * 60)

    print("[*] 构建索引...")
    wem_json_index = build_wem_res_json_index()
    print(f"    WemResJson: {len(wem_json_index)} 个")

    wem_file_index = build_wem_res_wem_index()
    print(f"    WemResWem文件: {len(wem_file_index)} 个")

    bank_wem_ids = build_bank_res_wem_ids()
    print(f"    BankRes引用WemID: {len(bank_wem_ids)} 个")

    txtp_wem_index = build_txtp_wem_index()
    print(f"    txtp引用WemID: {len(txtp_wem_index)} 个")

    used_wem_ids = build_used_wem_ids()
    print(f"    sound_wem_mapping_export中已使用: {len(used_wem_ids)} 个")

    results = []
    in_wem_dir_count = 0
    in_bank_res_count = 0
    in_txtp_count = 0

    for wem_id in sorted(wem_json_index.keys()):
        if wem_id in used_wem_ids:
            continue

        info = wem_json_index[wem_id]
        coord = info['Coord']
        wem_filename = wem_file_index.get(coord, '')
        wem_path = str(WEM_RES_WEM_DIR / wem_filename) if wem_filename else ''

        result = {
            'WemID': wem_id,
            'Coord': coord,
            'JsonFile': info['JsonFile'],
            'IsStreaming': info['IsStreaming'],
            'WemFile': wem_filename,
            'WemPath': wem_path,
            'FoundInBankRes': '否',
            'TxtpFiles': '',
        }

        if wem_filename:
            in_wem_dir_count += 1

        if wem_id in bank_wem_ids:
            result['FoundInBankRes'] = '是'
            in_bank_res_count += 1

        if wem_id in txtp_wem_index:
            result['TxtpFiles'] = ';'.join(txtp_wem_index[wem_id])
            in_txtp_count += 1

        results.append(result)

    print("=" * 60)
    print("[统计]")
    print(f"[*] 总WEM数(过滤前): {len(wem_json_index)}")
    print(f"[*] 已在映射中导出(已过滤): {len(used_wem_ids)}")
    print(f"[*] 过滤后WEM数: {len(results)}")
    print(f"[*] 在WemResWem中有文件: {in_wem_dir_count}")
    print(f"[*] 在BankRes中有引用: {in_bank_res_count}")
    print(f"[*] 在txtp中有引用: {in_txtp_count}")
    print("=" * 60)

    with open(OUTPUT_CSV, 'w', encoding='utf-8', newline='') as f:
        fieldnames = [
            'WemID', 'Coord', 'JsonFile', 'IsStreaming',
            'WemFile', 'WemPath',
            'FoundInBankRes', 'TxtpFiles'
        ]
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for result in results:
            writer.writerow(result)

    print(f"[*] 结果已保存到: {OUTPUT_CSV}")

    if results:
        print("\n[样本数据]")
        for i, r in enumerate(results[:10]):
            info = [f"Coord({r['Coord']})"]
            if r['WemFile']:
                info.append("有WemFile")
            if r['FoundInBankRes'] == '是':
                info.append("BankRes有引用")
            if r['TxtpFiles']:
                info.append("Txtp有引用")
            info_str = ', '.join(info)
            print(f"  {i+1}. WemID={r['WemID']} -> {info_str}")
        if len(results) > 10:
            print(f"  ... 还有 {len(results) - 10} 个")


if __name__ == "__main__":
    main()
