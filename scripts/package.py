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
for path in required + [build / "OpenHand.pdb", root / "src" / "OpenHand" / "modicon.png"]:
    if path.is_file():
        entries.append((path, path.name))
# Ship binaries plus game assets: the game compiles any .cs files found in a
# mod at runtime without Harmony or full BCL references, which breaks the mod.
assets_root = root / "assets"
if assets_root.is_dir():
    for path in sorted(assets_root.rglob("*")):
        if path.is_file():
            entries.append((path, path.relative_to(assets_root.parent)))

archive.parent.mkdir(parents=True, exist_ok=True)
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
    for path, archive_path in entries:
        write_entry(output, path, archive_path)

print(archive)
