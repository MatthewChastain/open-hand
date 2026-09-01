#!/usr/bin/env python3
import pathlib
import sys
import zipfile

# Fixed timestamp (2000-01-01 00:00:00) so identical inputs build identical zips.
DETERMINISTIC_TIMESTAMP = (2000, 1, 1, 0, 0, 0)

root = pathlib.Path(__file__).resolve().parents[1]
version = sys.argv[1] if len(sys.argv) > 1 else "0.1.0"
build = root / "src" / "OpenHand" / "bin" / "Release" / "net10.0"
archive = root / "artifacts" / f"openhand_{version}.zip"

required = [build / "OpenHand.dll", build / "modinfo.json"]
missing = [path for path in required if not path.is_file()]
if missing:
    raise SystemExit(f"Build first; missing: {', '.join(str(path) for path in missing)}")


def write_entry(output, path, archive_path):
    info = zipfile.ZipInfo(str(archive_path), date_time=DETERMINISTIC_TIMESTAMP)
    info.compress_type = zipfile.ZIP_DEFLATED
    info.external_attr = 0o644 << 16
    output.writestr(info, path.read_bytes())


entries = []
for path in required + [build / "OpenHand.pdb"]:
    if path.is_file():
        entries.append((path, path.name))
source_root = root / "src" / "OpenHand"
sources = [
    path
    for path in sorted(source_root.rglob("*.cs"))
    # Skip build intermediates (obj/*.g.cs, AssemblyInfo.cs) and any copies under bin/.
    if not {"bin", "obj"} & set(path.relative_to(source_root).parts)
]
for path in sources:
    entries.append((path, pathlib.Path("src") / path.relative_to(source_root)))

archive.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
    for path, archive_path in entries:
        write_entry(output, path, archive_path)

print(archive)
