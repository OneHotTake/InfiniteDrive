#!/bin/bash

# Set variables
PROJECT_DIR="."  # Adjust this if you want a specific directory
OUTPUT_FILE="repo_dump.txt"
OUT_DIR="$PROJECT_DIR/dump_parts"

# Delete existing parts
if [ -d "$OUT_DIR" ]; then
    echo "Deleting existing parts in '$OUT_DIR'..."
    rm -rf "$OUT_DIR"
fi

# Create output directory for the new parts
mkdir -p "$OUT_DIR"

# Run the Python script to dump and split the repo
echo "Running repo_to_txt.py to capture source files..."
python3 repo_to_txt.py "$PROJECT_DIR" "$OUTPUT_FILE" --max-mb 0.9

echo "Refresh completed."

