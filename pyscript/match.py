#!/usr/bin/env python3
import os
import sys
import hashlib
import struct
import argparse

def _read_chunk_header(f):
    """读取 WAV 块头（4字节 ID + 4字节小端长度），返回 (id, size)"""
    header = f.read(8)
    if len(header) < 8:
        return None, 0
    chunk_id = header[:4]
    chunk_size = struct.unpack('<I', header[4:8])[0]
    return chunk_id, chunk_size


def get_wav_audio_data_size(file_path):
    """
    获取 WAV 文件中音频数据（data chunk）的大小（字节数）。
    返回 None 表示文件不是标准 WAV 或读取失败。
    """
    try:
        with open(file_path, "rb") as f:
            riff = f.read(4)
            if riff != b'RIFF':
                return None
            # 跳过文件大小字段
            f.read(4)
            wave = f.read(4)
            if wave != b'WAVE':
                return None

            # 遍历 chunks，找到 "data" chunk
            while True:
                chunk_id, chunk_size = _read_chunk_header(f)
                if chunk_id is None:
                    return None
                if chunk_id == b'data':
                    return chunk_size
                # 跳过当前 chunk 的数据部分（对齐到偶数边界）
                if chunk_size % 2 != 0:
                    chunk_size += 1
                f.seek(chunk_size, os.SEEK_CUR)
    except Exception:
        return None


def calculate_md5(file_path):
    """计算文件的 MD5 哈希值（数字指纹）"""
    hash_md5 = hashlib.md5()
    try:
        with open(file_path, "rb") as f:
            for chunk in iter(lambda: f.read(4096), b""):
                hash_md5.update(chunk)
        return hash_md5.hexdigest()
    except Exception as e:
        print(f" 错误: 无法读取文件 {file_path} ({e})", file=sys.stderr)
        return None


def calculate_wav_audio_md5(file_path):
    """
    只计算 WAV 文件中音频数据（data chunk）的 MD5。
    这样即使元数据块不同，只要音频内容相同就能匹配。
    """
    hash_md5 = hashlib.md5()
    try:
        with open(file_path, "rb") as f:
            riff = f.read(4)
            if riff != b'RIFF':
                return None
            # 跳过文件大小字段
            f.read(4)
            wave = f.read(4)
            if wave != b'WAVE':
                return None

            # 遍历 chunks，定位 "data" chunk
            while True:
                chunk_id, chunk_size = _read_chunk_header(f)
                if chunk_id is None:
                    return None
                if chunk_id == b'data':
                    # 读取音频数据并计算 MD5
                    remaining = chunk_size
                    while remaining > 0:
                        read_size = min(4096, remaining)
                        data = f.read(read_size)
                        if not data:
                            break
                        hash_md5.update(data)
                        remaining -= len(data)
                    return hash_md5.hexdigest()
                # 跳过当前 chunk（对齐到偶数边界）
                if chunk_size % 2 != 0:
                    chunk_size += 1
                f.seek(chunk_size, os.SEEK_CUR)
    except Exception as e:
        print(f" 错误: 无法读取 WAV 音频数据 {file_path} ({e})", file=sys.stderr)
        return None


def find_original_name(target_file, original_dir):
    # 1. 校验输入
    if not os.path.isfile(target_file):
        print(f"❌ 错误: 目标文件不存在 -> {target_file}", file=sys.stderr)
        return None
    if not os.path.isdir(original_dir):
        print(f"❌ 错误: 原文件夹路径不存在 -> {original_dir}", file=sys.stderr)
        return None

    # 2. 判断目标文件是否为 WAV，选择对应的指纹计算方式
    is_wav = target_file.lower().endswith('.wav')
    if is_wav:
        print("检测到 WAV 文件，仅对比音频数据（忽略元数据差异）...", file=sys.stderr)
        target_md5 = calculate_wav_audio_md5(target_file)
        target_audio_size = get_wav_audio_data_size(target_file)
        if target_audio_size is None:
            print("⚠️ 警告: 无法解析目标 WAV 文件结构", file=sys.stderr)
    else:
        target_md5 = calculate_md5(target_file)
        target_audio_size = None

    print(f"目标文件的 MD5 指纹: {target_md5}", file=sys.stderr)
    if not target_md5:
        return None

    # 3. 遍历原文件夹，寻找匹配的指纹
    print(f"正在扫描原文件夹并进行比对...", file=sys.stderr)
    for root, _, files in os.walk(original_dir):
        for file in files:
            full_path = os.path.join(root, file)

            # WAV 文件：按音频数据大小初筛
            if is_wav:
                cur_audio_size = get_wav_audio_data_size(full_path)
                if cur_audio_size is None or cur_audio_size != target_audio_size:
                    continue
                current_md5 = calculate_wav_audio_md5(full_path)
            else:
                # 非 WAV 文件：按文件大小初筛
                if os.path.getsize(full_path) != os.path.getsize(target_file):
                    continue
                current_md5 = calculate_md5(full_path)

            if current_md5 == target_md5:
                return file

    return None

if __name__ == "__main__":
    # 配置命令行参数解析
    parser = argparse.ArgumentParser(
        description="通过音频内容数字指纹（MD5），在原文件夹中匹配并找回乱码文件的原文件名。"
    )
    parser.add_argument(
        "-f", "--file", 
        required=True, 
        help="需要查找原名的乱码文件路径"
    )
    parser.add_argument(
        "-d", "--dir", 
        required=True, 
        help="存放原本文件的文件夹路径"
    )

    args = parser.parse_args()

    # 执行查找
    original_name = find_original_name(args.file, args.dir)

    print("-" * 40)
    if original_name:
        print(f"🎉 成功找到原文件名!")
        print(f"👉 原始名称: {original_name}")
    else:
        print("❌ 未在指定文件夹中找到内容完全匹配的原文件。")