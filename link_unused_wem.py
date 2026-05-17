#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
从WemResJson构建所有WEM的索引，交叉引用banks.xml和WemResWem
"""

import csv
import xml.etree.ElementTree as ET
import json
import re
from pathlib import Path


BASE_DIR = Path(r"D:\Odradek-wwise-namer")
BANKS_XML = BASE_DIR / "Extracted_Banks" / "banks.xml"
WEM_RES_JSON_DIR = BASE_DIR / "WemResJson"
WEM_RES_WEM_DIR = Path("G:\ds2 unpack\wems\WemResWem")
OUTPUT_CSV = BASE_DIR / "unused_wem_with_banks.csv"


def build_wem_res_json_index():
    """从WemResJson构建 WemID -> {坐标, IsStreaming, WemSize} 索引"""
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
                'WemSize': data.get('WemSize', ''),
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


def build_bank_media_index():
    media_index = {}
    if not BANKS_XML.exists():
        print(f"[!] 找不到banks.xml: {BANKS_XML}")
        return media_index

    try:
        tree = ET.parse(BANKS_XML)
        root = tree.getroot()
        for bank in root.findall(".//root"):
            bank_filename = bank.get("filename")
            if not bank_filename:
                continue
            for media_header in bank.findall(".//obj[@na='MediaHeader']"):
                sid_field = media_header.find(".//fld[@na='id']")
                if sid_field is None:
                    continue
                sid_value = sid_field.get("value") or sid_field.get("va")
                if sid_value is None:
                    continue
                try:
                    media_id = int(sid_value) & 0xFFFFFFFF
                    if media_id not in media_index:
                        media_index[media_id] = []
                    media_index[media_id].append(bank_filename)
                except ValueError:
                    continue
    except Exception as e:
        print(f"[!] 解析banks.xml失败: {e}")
    return media_index


def main():
    print("=" * 60)
    print("从WemResJson构建WEM索引，交叉引用banks.xml和WemResWem")
    print("=" * 60)

    print("[*] 构建索引...")
    wem_json_index = build_wem_res_json_index()
    print(f"    WemResJson: {len(wem_json_index)} 个")

    wem_file_index = build_wem_res_wem_index()
    print(f"    WemResWem文件: {len(wem_file_index)} 个")

    bank_media_index = build_bank_media_index()
    print(f"    banks.xml: {len(bank_media_index)} media")

    results = []
    in_wem_dir_count = 0
    in_banks_count = 0
    no_bank_count = 0

    for wem_id in sorted(wem_json_index.keys()):
        info = wem_json_index[wem_id]
        coord = info['Coord']
        wem_filename = wem_file_index.get(coord, '')
        wem_path = str(WEM_RES_WEM_DIR / wem_filename) if wem_filename else ''

        result = {
            'WemID': wem_id,
            'Coord': coord,
            'JsonFile': info['JsonFile'],
            'IsStreaming': info['IsStreaming'],
            'WemSize': info['WemSize'],
            'WemFile': wem_filename,
            'WemPath': wem_path,
            'FoundInBanks': '否',
            'Banks': '',
            'BankCount': 0,
        }

        if wem_filename:
            in_wem_dir_count += 1

        if wem_id in bank_media_index:
            banks = bank_media_index[wem_id]
            result['FoundInBanks'] = '是'
            result['Banks'] = ';'.join(banks)
            result['BankCount'] = len(banks)
            in_banks_count += 1
        else:
            no_bank_count += 1

        results.append(result)

    print("=" * 60)
    print("[统计]")
    print(f"[*] 总WEM数: {len(wem_json_index)}")
    print(f"[*] 在WemResWem中有文件: {in_wem_dir_count}")
    print(f"[*] 在banks.xml中有引用: {in_banks_count}")
    print(f"[*] 在banks.xml中无引用: {no_bank_count}")
    print("=" * 60)

    with open(OUTPUT_CSV, 'w', encoding='utf-8', newline='') as f:
        fieldnames = [
            'WemID', 'Coord', 'JsonFile', 'IsStreaming', 'WemSize',
            'WemFile', 'WemPath',
            'FoundInBanks', 'BankCount', 'Banks'
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
            if r['FoundInBanks'] == '是':
                info.append(f"banks({r['BankCount']})")
            info_str = ', '.join(info)
            print(f"  {i+1}. WemID={r['WemID']} -> {info_str}")
        if len(results) > 10:
            print(f"  ... 还有 {len(results) - 10} 个")


if __name__ == "__main__":
    main()
