"""Seal only explicit production build inputs, never credentials or evidence."""
import hashlib
import pathlib

root = pathlib.Path(__file__).resolve().parents[2]
package = pathlib.Path(__file__).resolve().parent
inputs = []
for project in ('PointOfSale.API', 'PointOfSale.Core', 'PointOfSale.Infrastructure'):
    for path in (root / project).rglob('*'):
        relative = path.relative_to(root)
        if not path.is_file() or any(part in ('bin', 'obj', 'node_modules') for part in relative.parts):
            continue
        if '.local.' in path.name or path.name.startswith('.env'):
            continue
        if path.suffix in ('.cs', '.csproj', '.json', '.resx', '.sql', '.cshtml', '.html',
                           '.js', '.css', '.svg', '.xml', '.config', '.tt', '.png', '.jpg', '.woff2'):
            inputs.append(path)
inputs.extend([root/'global.json', package/'successor.Dockerfile', package/'build-successor-image.sh'])
lines = [hashlib.sha256(path.read_bytes()).hexdigest() + '  ' + path.relative_to(root).as_posix()
         for path in sorted(inputs)]
manifest = package/'source-input-SHA256SUMS.txt'
manifest.write_text('\n'.join(lines)+'\n', encoding='utf-8')
print(hashlib.sha256(manifest.read_bytes()).hexdigest())
