#!/bin/bash
# =============================================================================
# Repo Dump Script
# =============================================================================
# Dumps project files into <1MB text files for AI chatbot uploads.
#
# Usage: ./repo_dump.sh {source|all} [project_dir]
#
# Commands:
#   source - Dumps source code files only (.cs, .js, .ts, .html, .css, .py)
#   all    - Dumps all files including tests
#
# =============================================================================

set -euo pipefail

# Configuration
SEPARATOR="================================================================================"
MAX_BYTES=$(( 950 * 1024 ))  # ~0.95 MB, safe margin under 1MB
OUTPUT_DIR="./dump_parts"

# File extensions for each mode
SOURCE_EXTENSIONS="*.cs *.js *.ts *.html *.css *.py *.pyi *.json *.xml *.sh"
ALL_EXTENSIONS="*"

# Directories to skip
SKIP_DIRS=".git .svn .hg __pycache__ .mypy_cache .pytest_cache node_modules .venv venv env .env dist build .idea .vscode .ai documentation bin obj bin/Release bin/Debug dump_parts .next"

# Colors
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

log_info()    { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn()    { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error()   { echo -e "${RED}[ERROR]${NC} $1"; }

usage() {
    echo "Usage: $0 {source|all} [project_dir]"
    echo ""
    echo "Commands:"
    echo "  source - Dump source code files only"
    echo "  all    - Dump all files including tests"
    echo ""
    echo "Arguments:"
    echo "  project_dir - Project directory (default: current directory)"
    exit 1
}

# Check arguments
if [ $# -lt 1 ]; then
    usage
fi

MODE="$1"
PROJECT_DIR="${2:-.}"

# Validate mode
case "$MODE" in
    source|all) ;;
    *) usage ;;
esac

# Set extensions based on mode
if [ "$MODE" = "source" ]; then
    MODE_DESC="source code"
else
    MODE_DESC="all files"
fi

# Resolve absolute path to project dir
PROJECT_DIR="$(cd "$PROJECT_DIR" && pwd)"

log_info "Dumping $MODE_DESC from: $PROJECT_DIR"

# Clean output directory
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Find files and sort them
build_file_list() {
    local mode="$1"
    local files=()

    if [ "$mode" = "all" ]; then
        # Find all files (no extension filter)
        while IFS= read -r -d '' file; do
            # Skip if in a skipped directory
            local skip=0
            for skip_dir in $SKIP_DIRS; do
                if [[ "$file" == *"/$skip_dir/"* ]] || [[ "$file" == *"/$skip_dir" ]]; then
                    skip=1
                    break
                fi
            done

            if [ "$skip" -eq 0 ]; then
                # Make path relative to project dir
                local rel_path="${file#$PROJECT_DIR/}"
                files+=("$rel_path")
            fi
        done < <(find "$PROJECT_DIR" -type f -print0 2>/dev/null || true)
    else
        # Filter by extensions for source mode
        for ext in $SOURCE_EXTENSIONS; do
            while IFS= read -r -d '' file; do
                # Skip if in a skipped directory
                local skip=0
                for skip_dir in $SKIP_DIRS; do
                    if [[ "$file" == *"/$skip_dir/"* ]] || [[ "$file" == *"/$skip_dir" ]]; then
                        skip=1
                        break
                    fi
                done

                if [ "$skip" -eq 0 ]; then
                    # Make path relative to project dir
                    local rel_path="${file#$PROJECT_DIR/}"
                    files+=("$rel_path")
                fi
            done < <(find "$PROJECT_DIR" -type f -name "$ext" -print0 2>/dev/null || true)
        done
    fi

    # Sort and deduplicate
    printf '%s\n' "${files[@]}" | sort -u
}

# Get file list
mapfile -t FILES < <(build_file_list "$MODE")
FILE_COUNT=${#FILES[@]}

if [ "$FILE_COUNT" -eq 0 ]; then
    log_error "No files found matching criteria"
    exit 1
fi

log_info "Found $FILE_COUNT files"

# Create a temporary directory for individual file dumps
TEMP_DIR=$(mktemp -d)
trap "rm -rf $TEMP_DIR" EXIT

# Dump each file to a temp file
for file in "${FILES[@]}"; do
    full_path="$PROJECT_DIR/$file"
    temp_file="$TEMP_DIR/${file//\//_}"
    mkdir -p "$(dirname "$temp_file")"

    {
        echo "$SEPARATOR"
        echo "FILE: $file"
        echo "$SEPARATOR"
        cat "$full_path" 2>/dev/null || echo "[ERROR reading file]"
        echo ""
    } > "$temp_file"

    # Check file size and warn if too large
    size=$(stat -c%s "$temp_file" 2>/dev/null || echo 0)
    if [ "$size" -gt "$MAX_BYTES" ]; then
        log_warn "File $file is $((size / 1024))KB and exceeds max size - will be in its own part"
    fi
done

# Split into parts, keeping file blocks intact
HEADER="$SEPARATOR
REPO_DUMP: $PROJECT_DIR
MODE: $MODE
DATE: $(date -Iseconds)
FILE_COUNT: $FILE_COUNT
$SEPARATOR
"

part_num=1
current_size=${#HEADER}
current_files=0
part_output="$OUTPUT_DIR/dump_part$(printf "%02d" "$part_num").txt"

# Write header to first part
echo -n "$HEADER" > "$part_output"

# Process each file block
log_info "Creating parts..."
for temp_file in "$TEMP_DIR"/*; do
    [ -f "$temp_file" ] || continue

    file_size=$(stat -c%s "$temp_file" 2>/dev/null || echo 0)

    # Check if adding this file would exceed limit
    if [ $((current_size + file_size)) -gt "$MAX_BYTES" ] && [ "$current_files" -gt 0 ]; then
        # Flush current part
        current_size_kb=$((current_size / 1024))
        log_info "  Part $part_num: ${current_size_kb}KB ($current_files files)"
        part_num=$((part_num + 1))
        current_size=${#HEADER}
        current_files=0
        part_output="$OUTPUT_DIR/dump_part$(printf "%02d" "$part_num").txt"
        echo -n "$HEADER" > "$part_output"
    fi

    # Append file block
    cat "$temp_file" >> "$part_output"
    echo "" >> "$part_output"
    current_size=$((current_size + file_size))
    current_files=$((current_files + 1))
done

# Flush final part
current_size_kb=$((current_size / 1024))
log_info "  Part $part_num: ${current_size_kb}KB ($current_files files)"

# Final summary
log_info "Created $part_num part(s) in '$OUTPUT_DIR/'"
log_info "Upload in order: dump_part01.txt, dump_part02.txt, ..."
