"""Validate protected operator input; never print or hash the credential."""
import os
import pathlib
import stat
import sys

path = pathlib.Path(sys.argv[1])
if not path.is_absolute():
    raise SystemExit("Credential path must be absolute")
directory_flags = os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW
directory = os.open("/", directory_flags)
try:
    for component in path.parts[1:-1]:
        child = os.open(component, directory_flags, dir_fd=directory)
        os.close(directory)
        directory = child
    parent = os.fstat(directory)
    if parent.st_uid != os.getuid() or stat.S_IMODE(parent.st_mode) != 0o700:
        raise SystemExit("Credential parent must be operator-owned mode 0700")
    descriptor = os.open(path.name, os.O_RDONLY | os.O_NOFOLLOW, dir_fd=directory)
    try:
        info = os.fstat(descriptor)
        if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or info.st_uid != os.getuid():
            raise SystemExit("Credential must be a singly-linked operator-owned regular file")
        if stat.S_IMODE(info.st_mode) not in (0o400, 0o600):
            raise SystemExit("Credential permissions must be 0400 or 0600")
        token = os.read(descriptor, 1024)
        if not token or len(token) >= 1024 or not all(33 <= byte <= 126 for byte in token):
            raise SystemExit("Credential must contain one raw ASCII token without whitespace")
    finally:
        os.close(descriptor)
finally:
    os.close(directory)
