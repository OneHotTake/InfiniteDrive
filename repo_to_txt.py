#!/usr/bin/env python3
"""
repo_to_txt.py

Captures all source and documentation files from a project directory
into a single .txt file suitable for uploading to an AI assistant.

Usage:
    python repo_to_txt.py [project_dir] [output_file] [--ext py js ts md txt yaml yml toml cfg ini sh]
"""

import os
import sys
import argparse
from pathlib import Path
from datetime import datetime

# Default file extensions to capture
DEFAULT_EXTENSIONS = {
    # Python
    ".py", ".pyi",
    # Web / JS
    ".js", ".ts", ".jsx", ".tsx", ".html", ".css",
    # Docs / Config
    ".md", ".txt", ".rst",
    ".yaml", ".yml", ".toml", ".cfg", ".ini", ".env.example",
    # Shell
    ".sh", ".bash",
    # Data
    ".json", ".xml",
    # Dockerfile / Makefile
    ".dockerfile",
}

FILENAME_MATCHES = {
    "Dockerfile", "Makefile", "Procfile", ".env.example",
}

# Directories to always skip
SKIP_DIRS = {
    ".git", ".svn", ".hg",
    "__pycache__", ".mypy_cache", ".pytest_cache", ".ruff_cache",
    "node_modules", ".venv", "venv", "env", ".env",
    "dist", "build", ".dist", ".build",
    ".idea", ".vscode",
    "*.egg-info",
}

SEPARATOR = "=" * 80


def should_include(path: Path, extensions: set[str]) -> bool:
    """Return True if this file should be included in the dump."""
    if path.name in FILENAME_MATCHES:
        return True
    return path.suffix.lower() in extensions


def dump_repo(project_dir: Path, output_file: Path, extensions: set[str]) -> None:
    """Walk project_dir and write all matching files to output_file."""
    project_dir = project_dir.resolve()
    file_count = 0
    skipped_count = 0

    with output_file.open("w", encoding="utf-8", errors="replace") as out:
        # Write header
        out.write(f"{SEPARATOR}\n")
        out.write(f"REPO DUMP\n")
        out.write(f"Source : {project_dir}\n")
        out.write(f"Created: {datetime.now().isoformat(timespec='seconds')}\n")
        out.write(f"{SEPARATOR}\n\n")

        for root, dirs, files in os.walk(project_dir, topdown=True):
            # Prune skipped directories in-place so os.walk won't descend into them
            dirs[:] = sorted(
                d for d in dirs
                if d not in SKIP_DIRS and not d.endswith(".egg-info")
            )
            files = sorted(files)

            root_path = Path(root)
            rel_root = root_path.relative_to(project_dir)

            for filename in files:
                file_path = root_path / filename
                rel_path = rel_root / filename

                if not should_include(file_path, extensions):
                    skipped_count += 1
                    continue

                # --- File header ---
                out.write(f"{SEPARATOR}\n")
                out.write(f"FILE: {rel_path}\n")
                out.write(f"{SEPARATOR}\n")

                try:
                    content = file_path.read_text(encoding="utf-8", errors="replace")
                    out.write(content)
                    if not content.endswith("\n"):
                        out.write("\n")
                except Exception as e:
                    out.write(f"[ERROR reading file: {e}]\n")

                out.write("\n")
                file_count += 1

        # Footer
        out.write(f"{SEPARATOR}\n")
        out.write(f"END OF DUMP — {file_count} files captured, {skipped_count} skipped\n")
        out.write(f"{SEPARATOR}\n")

    print(f"✓  Wrote {file_count} files → {output_file}")
    print(f"   Skipped {skipped_count} non-matching files")
    print(f"   Output size: {output_file.stat().st_size / 1024:.1f} KB")


def main():
    parser = argparse.ArgumentParser(
        description="Dump a project directory into a single .txt file for AI upload."
    )
    parser.add_argument(
        "project_dir",
        nargs="?",
        default=".",
        help="Root directory of the project (default: current directory)",
    )
    parser.add_argument(
        "output_file",
        nargs="?",
        default="repo_dump.txt",
        help="Output file path (default: repo_dump.txt)",
    )
    parser.add_argument(
        "--ext",
        nargs="+",
        metavar="EXT",
        help="Override file extensions to include (e.g. --ext py md yaml)",
    )
    parser.add_argument(
        "--add-ext",
        nargs="+",
        metavar="EXT",
        help="Add extra extensions on top of the defaults (e.g. --add-ext lua rb)",
    )
    parser.add_argument(
        "--skip-dir",
        nargs="+",
        metavar="DIR",
        help="Additional directory names to skip",
    )

    args = parser.parse_args()

    # Build extension set
    if args.ext:
        extensions = {e if e.startswith(".") else f".{e}" for e in args.ext}
    else:
        extensions = set(DEFAULT_EXTENSIONS)

    if args.add_ext:
        extensions |= {e if e.startswith(".") else f".{e}" for e in args.add_ext}

    if args.skip_dir:
        SKIP_DIRS.update(args.skip_dir)

    project_dir = Path(args.project_dir)
    output_file = Path(args.output_file)

    if not project_dir.is_dir():
        print(f"Error: '{project_dir}' is not a directory.", file=sys.stderr)
        sys.exit(1)

    dump_repo(project_dir, output_file, extensions)


if __name__ == "__main__":
    main()

