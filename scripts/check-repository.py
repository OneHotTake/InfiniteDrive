#!/usr/bin/env python3
"""Dependency-free checks for current docs, metadata and accidental artifacts.

Run from any directory. Historical document bodies are excluded from link checks:
they retain references to removed implementations, not supported operating steps.
"""
from pathlib import Path
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]


def git_paths(*args):
    return subprocess.check_output(['git', '-C', str(ROOT), *args]).decode().split('\0')[:-1]


def main():
    errors = []
    paths = sorted(set(git_paths('ls-files', '-z', '--cached', '--others', '--exclude-standard')))
    paths = [name for name in paths if (ROOT / name).is_file()]
    ignored = git_paths('ls-files', '-z', '--cached', '--ignored', '--exclude-standard')
    for name in ignored:
        if (ROOT / name).is_file():
            errors.append(f'{name}: tracked despite .gitignore')
    banned_parts = {'.ai', 'newdocs', 'node_modules', 'bin', 'obj', 'libs', 'artifacts', '__pycache__'}
    banned_suffixes = {'.dll', '.pdb', '.db', '.sqlite', '.sqlite3', '.log', '.new', '.bak'}
    for name in paths:
        p = Path(name)
        if set(p.parts) & banned_parts or p.suffix in banned_suffixes or p.name in {'.DS_Store', 'Export.txt'}:
            errors.append(f'{name}: generated/private/scratch artifact in repository')

    docs = [name for name in paths if name.endswith('.md') and
            (not name.startswith('docs/archive/') or name == 'docs/archive/README.md')]
    # Inline links/images and reference-link definitions; ignore fenced code examples.
    link_pattern = re.compile(r'!?\[[^]\n]*\]\(<?([^\s)>]+)>?(?:\s+"[^"]*")?\)|^\s*\[[^]]+\]:\s*<?([^\s>]+)>?', re.M)
    for name in docs:
        content = (ROOT / name).read_text()
        lines, fence = [], None
        for line in content.splitlines():
            marker = re.match(r'^\s*(`{3,}|~{3,})', line)
            if marker:
                token = marker.group(1)
                if fence is None:
                    fence = token
                elif token[0] == fence[0] and len(token) >= len(fence):
                    fence = None
                continue
            if fence is None:
                lines.append(line)
        if fence is not None:
            errors.append(f'{name}: unclosed Markdown fence')
        for match in link_pattern.finditer('\n'.join(lines)):
            url = next(group for group in match.groups() if group is not None)
            parsed = urlsplit(url)
            if parsed.scheme or parsed.netloc or not parsed.path:
                continue
            target = (ROOT / name).parent / unquote(parsed.path)
            if parsed.path.startswith('/') or not target.resolve().is_relative_to(ROOT):
                errors.append(f'{name}: non-portable local link {url}')
            elif not target.exists():
                errors.append(f'{name}: missing link target {url}')
            elif target.is_file() and target.resolve().relative_to(ROOT).as_posix() not in paths:
                errors.append(f'{name}: target is untracked or has wrong letter case: {url}')

    project = ET.parse(ROOT / 'InfiniteDrive.csproj').getroot()
    manifest = json.loads((ROOT / 'plugin.json').read_text())
    versions = {project.findtext(f'.//{tag}') for tag in ('Version', 'AssemblyVersion', 'FileVersion')}
    if versions != {manifest['version']}:
        errors.append('Project assembly/file/package versions must match plugin.json')
    if project.findtext('.//TargetFramework') != manifest['framework']:
        errors.append('TargetFramework must match plugin.json')
    required = ['README.md', 'LICENSE', 'AGENTS.md', 'CONTRIBUTING.md', 'docs/README.md',
                'docs/dev-guide.md', 'docs/architecture.md', 'docs/SECURITY.md']
    for name in required:
        if not (ROOT / name).is_file():
            errors.append(f'Missing required entry point: {name}')
    if errors:
        print('\n'.join(f'ERROR {error}' for error in errors))
        return 1
    print(f'PASS: {len(paths)} repository files; {len(docs)} current Markdown files; release metadata consistent.')
    print('Historical bodies and remote URLs are not link-checked. This is not a secret scanner.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
