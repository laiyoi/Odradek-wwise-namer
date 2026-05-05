#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
联系未被使用的WEM文件与banks.xml和WwiseID中的信息
"""

import csv
import xml.etree.ElementTree as ET
import json
from pathlib import Path


# 配置
BASE_DIR = Path(r"D:\Odradek-wwise-namer")
MISSING_WEM_CSV = BASE_DIR / "missing_wem_files.csv"
BANKS_XML = BASE_DIR / "Extracted_Banks" / "banks.xml"
WWISE_ID_DIR = BASE_DIR / "WwiseID"
WEM_RES_DIR = BASE_DIR / "WemRes"
OUTPUT_CSV = BASE_DIR / "unused_wem_with_banks.csv"


def build_bank_media_index():
    """从banks.xml构建media索引和bank索引"""
    media_index = {}
    bank_id_index = {}  # bank_id -> bank_filename
    if not BANKS_XML.exists():
        print(f"[!] 找不到banks.xml: {BANKS_XML}")
        return media_index, bank_id_index
    
    try:
        print(f"[*] 正在解析banks.xml...")
        tree = ET.parse(BANKS_XML)
        root = tree.getroot()
        
        # 遍历所有bank
        for bank in root.findall(".//root"):
            bank_filename = bank.get("filename")
            if not bank_filename:
                continue
            
            # 找dwSoundBankID
            bank_id_field = bank.find(".//fld[@na='dwSoundBankID']")
            if bank_id_field is not None:
                bank_id_value = bank_id_field.get("value") or bank_id_field.get("va")
                if bank_id_value is not None:
                    try:
                        bank_id = int(bank_id_value) & 0xFFFFFFFF
                        bank_id_index[bank_id] = bank_filename
                    except ValueError:
                        pass
            
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
        print(f"[!] 解析banks.xml失败: {e}")
    
    print(f"[*] banks.xml解析完成，共 {len(media_index)} 个media, {len(bank_id_index)} 个bank")
    return media_index, bank_id_index


def read_missing_wem_csv():
    """读取missing_wem_files.csv"""
    missing_wems = []
    if not MISSING_WEM_CSV.exists():
        print(f"[!] 找不到missing_wem_files.csv: {MISSING_WEM_CSV}")
        return missing_wems
    
    try:
        with open(MISSING_WEM_CSV, 'r', encoding='utf-8') as f:
            reader = csv.DictReader(f)
            for row in reader:
                try:
                    wem_id = int(row['WemID'])
                    missing_wems.append({
                        'WemID': wem_id,
                        'Filename': row['Filename'],
                        'Path': row['Path']
                    })
                except (KeyError, ValueError) as e:
                    print(f"[!] 解析行失败: {row}, 错误: {e}")
                    continue
    except Exception as e:
        print(f"[!] 读取missing_wem_files.csv失败: {e}")
    
    print(f"[*] 读取到 {len(missing_wems)} 个未使用的WEM")
    return missing_wems


def build_wwise_id_index():
    """从WwiseID目录构建索引"""
    wwise_id_index = {}
    if not WWISE_ID_DIR.exists():
        print(f"[!] 找不到WwiseID目录: {WWISE_ID_DIR}")
        return wwise_id_index
    
    print(f"[*] 正在解析WwiseID目录...")
    count = 0
    
    for json_file in WWISE_ID_DIR.glob("WwiseID_*.json"):
        try:
            with open(json_file, 'r', encoding='utf-8') as f:
                data = json.load(f)
            
            if 'Id' in data:
                wwise_id = int(data['Id']) & 0xFFFFFFFF
                # 从文件名中提取坐标 (WwiseID_group_index.json)
                filename = json_file.stem
                parts = filename.split('_')
                if len(parts) >= 3:
                    group = parts[1]
                    index = parts[2]
                    coord = f"{group}:{index}"
                    
                    wwise_id_index[wwise_id] = {
                        'Coord': coord,
                        'File': str(json_file.name)
                    }
                    count += 1
        except Exception as e:
            continue
    
    print(f"[*] WwiseID解析完成，共 {count} 个")
    return wwise_id_index


def build_wem_res_index():
    """从WemRes目录构建索引，通过文件名中的坐标"""
    wem_res_index = {}
    if not WEM_RES_DIR.exists():
        print(f"[!] 找不到WemRes目录: {WEM_RES_DIR}")
        return wem_res_index
    
    print(f"[*] 正在解析WemRes目录...")
    count = 0
    
    for json_file in WEM_RES_DIR.glob("WwiseWemResource_*.json"):
        try:
            # 从文件名中提取坐标 (WwiseWemResource_group_index.json)
            filename = json_file.stem
            parts = filename.split('_')
            if len(parts) >= 3:
                group = parts[1]
                index = parts[2]
                coord = f"{group}:{index}"
                
                # 读取WemID
                with open(json_file, 'r', encoding='utf-8') as f:
                    data = json.load(f)
                
                if 'WemID' in data:
                    wem_id = int(data['WemID']) & 0xFFFFFFFF
                    wem_res_index[wem_id] = {
                        'Coord': coord,
                        'File': str(json_file.name)
                    }
                    count += 1
        except Exception as e:
            continue
    
    print(f"[*] WemRes解析完成，共 {count} 个")
    return wem_res_index


def link_unused_wem_with_banks():
    """联系未使用的WEM和banks.xml、WwiseID、WemRes"""
    print("="*60)
    print("联系未使用的WEM与banks.xml、WwiseID和WemRes")
    print("="*60)
    
    # 读取数据
    wwise_id_index = build_wwise_id_index()
    bank_media_index, bank_id_index = build_bank_media_index()
    wem_res_index = build_wem_res_index()
    missing_wems = read_missing_wem_csv()
    
    if not missing_wems:
        print("[!] 没有未使用的WEM数据")
        return
    
    # 联系信息
    results = []
    found_banks_count = 0
    found_wwise_count = 0
    found_wemres_count = 0
    not_found_count = 0
    
    for wem in missing_wems:
        wem_id = wem['WemID']
        
        result = {
            'WemID': wem_id,
            'Filename': wem['Filename'],
            'Path': wem['Path'],
            'FoundInBanks': '否',
            'Banks': '',
            'BankCount': 0,
            'BankIDs': '',  # 通过banks.xml找到的bank的dwSoundBankID
            'FoundInWwiseID': '否',
            'WwiseIDCoord': '',
            'WwiseIDFile': '',
            'FoundInWemRes': '否',
            'WemResCoord': '',
            'WemResFile': '',
            'FoundAny': '否'
        }
        
        # 检查banks.xml
        if wem_id in bank_media_index:
            banks = bank_media_index[wem_id]
            result['FoundInBanks'] = '是'
            result['Banks'] = ';'.join(banks)
            result['BankCount'] = len(banks)
            found_banks_count += 1
            
            # 通过bank查找对应的WwiseID（bank的dwSoundBankID对应WwiseID的Id）
            bank_ids = []
            for bank_filename in banks:
                # 从bank文件名解析bank_id（通过bank_id_index反向查找）
                for bid, bname in bank_id_index.items():
                    if bname == bank_filename:
                        bank_ids.append(str(bid))
                        break
            if bank_ids:
                result['BankIDs'] = ';'.join(bank_ids)
                # 通过bank_id查找WwiseID
                for bid_str in bank_ids:
                    try:
                        bid = int(bid_str)
                        if bid in wwise_id_index:
                            wwise_info = wwise_id_index[bid]
                            result['FoundInWwiseID'] = '是'
                            result['WwiseIDCoord'] = wwise_info['Coord']
                            result['WwiseIDFile'] = wwise_info['File']
                            found_wwise_count += 1
                            break
                    except ValueError:
                        pass
        
        # 检查WemRes
        if wem_id in wem_res_index:
            wemres_info = wem_res_index[wem_id]
            result['FoundInWemRes'] = '是'
            result['WemResCoord'] = wemres_info['Coord']
            result['WemResFile'] = wemres_info['File']
            found_wemres_count += 1
        
        # 标记是否找到任何一个
        if result['FoundInBanks'] == '是' or result['FoundInWwiseID'] == '是' or result['FoundInWemRes'] == '是':
            result['FoundAny'] = '是'
        else:
            not_found_count += 1
        
        results.append(result)
    
    # 输出结果
    print("="*60)
    print("[统计]")
    print(f"[*] 未使用的WEM总数: {len(missing_wems)}")
    print(f"[*] 在banks.xml中找到: {found_banks_count}")
    print(f"[*] 在WwiseID中找到: {found_wwise_count}")
    print(f"[*] 在WemRes中找到: {found_wemres_count}")
    print(f"[*] 找到至少一个: {sum(1 for r in results if r['FoundAny'] == '是')}")
    print(f"[*] 三个地方都未找到: {not_found_count}")
    print("="*60)
    
    # 写入输出文件
    try:
        with open(OUTPUT_CSV, 'w', encoding='utf-8', newline='') as f:
            fieldnames = [
                'WemID', 'Filename', 'Path',
                'FoundAny', 'FoundInWemRes', 'WemResCoord', 'WemResFile',
                'FoundInBanks', 'BankCount', 'Banks', 'BankIDs',
                'FoundInWwiseID', 'WwiseIDCoord', 'WwiseIDFile'
            ]
            writer = csv.DictWriter(f, fieldnames=fieldnames)
            writer.writeheader()
            for result in results:
                writer.writerow(result)
        print(f"[*] 结果已保存到: {OUTPUT_CSV}")
        
        # 打印前几个样本
        if results:
            print("\n[样本数据]")
            for i, r in enumerate(results[:10]):
                info = []
                if r['FoundInWemRes'] == '是':
                    info.append(f"WemRes({r['WemResCoord']})")
                if r['FoundInBanks'] == '是':
                    info.append(f"banks({r['BankCount']})[{r['BankIDs']}]")
                if r['FoundInWwiseID'] == '是':
                    info.append(f"wwiseid({r['WwiseIDCoord']})")
                info_str = ', '.join(info) if info else '未找到'
                print(f"  {i+1}. {r['Filename']} -> {info_str}")
            if len(results) > 10:
                print(f"  ... 还有 {len(results) - 10} 个")
                
    except Exception as e:
        print(f"[!] 写入输出文件失败: {e}")


if __name__ == "__main__":
    link_unused_wem_with_banks()
