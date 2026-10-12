#!/usr/bin/env python3
"""给项目源文件统一加 UTF-8 BOM（保证 Windows 下 csc / MSBuild 正确按 UTF-8 读取中文）。"""
import os

ROOT = "/var/minis/workspace/PrivacyIsland"
EXTS = {".cs", ".xaml", ".csproj", ".manifest", ".md", ".gitignore"}
SKIP_DIRS = {"bin", "obj", ".git"}

BOM = b"\xef\xbb\xbf"
count = 0

for dirpath, dirnames, filenames in os.walk(ROOT):
    dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
    for fn in filenames:
        _, ext = os.path.splitext(fn)
        if ext not in EXTS and fn != ".gitignore":
            continue
        path = os.path.join(dirpath, fn)
        with open(path, "rb") as f:
            data = f.read()
        if not data.startswith(BOM):
            with open(path, "wb") as f:
                f.write(BOM + data)
            count += 1
            print("BOM added:", os.path.relpath(path, ROOT))
        else:
            print("already has BOM:", os.path.relpath(path, ROOT))

print(f"\nDone. {count} files updated.")
