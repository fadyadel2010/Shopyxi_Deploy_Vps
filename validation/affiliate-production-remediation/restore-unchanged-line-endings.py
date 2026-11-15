"""Restore HEAD's exact line endings only where task-owned source text is unchanged.
No semantic content, whitespace, or protected user difference is rewritten.
"""
import difflib
import json
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
protected = {r['path'] for r in json.loads((root / 'docs/local-work/source-versions.json').read_text())}
protected |= {r['repositoryPath'] for r in json.loads((root / 'deploy/production-baseline/report-provenance.json').read_text())}
paths = subprocess.check_output(['git', 'diff', '--name-only', 'HEAD'], cwd=root).decode().splitlines()
changed = []
for name in paths:
    if name in protected or not name.endswith('.cs'):
        continue
    before = subprocess.check_output(['git', 'show', 'HEAD:' + name], cwd=root)
    target = root / name
    current = target.read_bytes()
    original_lines, current_lines = before.splitlines(keepends=True), current.splitlines(keepends=True)
    text = lambda lines: [line.rstrip(b'\r\n') for line in lines]
    matcher = difflib.SequenceMatcher(None, text(original_lines), text(current_lines), autojunk=False)
    restored = list(current_lines)
    for tag, a, b, c, d in matcher.get_opcodes():
        if tag == 'equal':
            restored[c:d] = original_lines[a:b]
    output = b''.join(restored)
    assert output.replace(b'\r\n', b'\n') == current.replace(b'\r\n', b'\n'), name
    if output != current:
        target.write_bytes(output)
        changed.append(name)
print('Restored unchanged original line endings in', len(changed), 'task-owned C# files; no semantic edits.')
