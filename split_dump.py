#!/usr/bin/env python3
"""
split_dump.py

Splits a repo_dump.txt into <1MB chunks, always breaking on FILE: boundaries.

Usage:
    python split_dump.py repo_dump.txt
    python split_dump.py repo_dump.txt --max-mb 0.9
    python split_dump.py myfile.txt --max-mb 0.8 --out-dir parts/
"""

import argparse
import sys
from pathlib import Path

SEPARATOR = "=" * 80
DEFAULT_MAX_BYTES = 950_000  # ~0.95 MB, safe margin under 1MB


def split_dump(input_file: Path, max_bytes: int, out_dir: Path) -> None:
    content = input_file.read_text(encoding="utf-8", errors="replace")

    # Split into chunks at FILE: boundaries (keep the separator as part of each section)
    sections = []
    current = []
    for line in content.splitlines(keepends=True):
        if line.startswith(SEPARATOR) and current:
            # Peek ahead logic: start a new section when we see a FILE: header block
            joined = "".join(current)
            # If the accumulated block looks like a complete header or file, save it
            sections.append(joined)
            current = [line]
        else:
            current.append(line)
    if current:
        sections.append("".join(current))

    # Now re-split more precisely: group by FILE: marker lines
    # Re-parse raw into logical file blocks
    raw = content
    file_marker = f"{SEPARATOR}\nFILE:"
    blocks = raw.split(f"{SEPARATOR}\nFILE:")

    # blocks[0] is the header preamble
    header = blocks[0]
    file_blocks = [f"{SEPARATOR}\nFILE:" + b for b in blocks[1:]]

    out_dir.mkdir(parents=True, exist_ok=True)
    stem = input_file.stem
    part_num = 1
    current_parts = []
    current_size = len(header.encode("utf-8"))
    total_parts_content = [header]

    def write_part(parts_content: list[str], num: int) -> Path:
        out_path = out_dir / f"{stem}_part{num:02d}.txt"
        out_path.write_text("".join(parts_content), encoding="utf-8")
        size_kb = out_path.stat().st_size / 1024
        print(f"  → {out_path.name}  ({size_kb:.1f} KB, {len(parts_content)-1} file blocks)")
        return out_path

    chunk_content = [header]
    chunk_size = len(header.encode("utf-8"))
    chunk_file_count = 0

    for block in file_blocks:
        block_size = len(block.encode("utf-8"))

        if block_size > max_bytes:
            # Single file is too large on its own — truncate it with a warning
            print(f"  ⚠ WARNING: A single file block is {block_size/1024:.1f} KB "
                  f"and exceeds the max. It will be written alone and may be too large.")

        if chunk_size + block_size > max_bytes and chunk_file_count > 0:
            # Flush current chunk
            write_part(chunk_content, part_num)
            part_num += 1
            chunk_content = [header]  # repeat header in each part for context
            chunk_size = len(header.encode("utf-8"))
            chunk_file_count = 0

        chunk_content.append(block)
        chunk_size += block_size
        chunk_file_count += 1

    # Write final chunk
    if chunk_file_count > 0:
        write_part(chunk_content, part_num)

    print(f"\n✓  Split into {part_num} part(s) in '{out_dir}/'")
    print(f"   Upload them in order: {stem}_part01.txt, {stem}_part02.txt, ...")


def main():
    parser = argparse.ArgumentParser(
        description="Split a repo_dump.txt into uploadable <1MB chunks."
    )
    parser.add_argument("input_file", help="The dump file to split (e.g. repo_dump.txt)")
    parser.add_argument(
        "--max-mb",
        type=float,
        default=0.95,
        help="Max size per part in MB (default: 0.95)",
    )
    parser.add_argument(
        "--out-dir",
        default="dump_parts",
        help="Output directory for parts (default: dump_parts/)",
    )
    args = parser.parse_args()

    input_file = Path(args.input_file)
    if not input_file.is_file():
        print(f"Error: '{input_file}' not found.", file=sys.stderr)
        sys.exit(1)

    max_bytes = int(args.max_mb * 1_000_000)
    out_dir = Path(args.out_dir)

    total_kb = input_file.stat().st_size / 1024
    print(f"Input: {input_file.name}  ({total_kb:.1f} KB)")
    print(f"Max part size: {args.max_mb} MB  →  splitting...\n")

    split_dump(input_file, max_bytes, out_dir)


if __name__ == "__main__":
    main()

