"""Stage only the independently authored patch and its documentation."""
from pathlib import Path
import hashlib
import json
import shutil
import zipfile

root = Path(__file__).resolve().parents[1]
manifest = json.loads((root / "Entelechia111Compat.json").read_text(encoding="utf-8-sig"))
version = manifest["version"]
if not all(part.isdecimal() for part in version.split(".")) or len(version.split(".")) != 3:
    raise SystemExit("Use a numeric release version before publishing.")
stage = root / "dist" / f"release-{version}" / "Entelechia111Compat"
content = root / "workshop" / "content"
dll = root / "src/Entelechia111Compat/bin/Release/net9.0/Entelechia111Compat.dll"
if not dll.is_file():
    raise SystemExit("Build the Release project before packaging.")
sources = {
    "Entelechia111Compat.dll": dll,
    "Entelechia111Compat.json": root / "Entelechia111Compat.json",
    "LICENSE": root / "LICENSE",
    "README.md": root / "README.md",
    "docs/ATTRIBUTION.md": root / "docs/ATTRIBUTION.md",
    "docs/VALIDATION.md": root / "docs/VALIDATION.md",
    "docs/AI_DISCLOSURE.md": root / "docs/AI_DISCLOSURE.md",
    "docs/LOCAL_GAMEPLAY_CHANGE.md": root / "docs/LOCAL_GAMEPLAY_CHANGE.md",
    f"docs/RELEASE_NOTES_{version}.md": root / f"docs/RELEASE_NOTES_{version}.md",
}
for folder in (stage, content):
    folder.mkdir(parents=True, exist_ok=True)
    unexpected = {p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file()} - sources.keys()
    if unexpected:
        raise SystemExit(f"Unexpected package files in {folder}: {sorted(unexpected)}")
    for name, source in sources.items():
        target = folder / name
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
    actual = {p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file()}
    if actual != sources.keys():
        raise SystemExit(f"Package file mismatch in {folder}")
archive = root / "dist" / f"Entelechia111Compat-v{version}.zip"
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as z:
    for path in sorted(p for p in stage.rglob("*") if p.is_file()):
        z.write(path, arcname=f"Entelechia111Compat/{path.relative_to(stage).as_posix()}")
digest = hashlib.sha256(archive.read_bytes()).hexdigest()
(root / "dist" / f"Entelechia111Compat-v{version}.sha256").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
print(json.dumps({"version":version,"archive":str(archive),"sha256":digest,"files":len(sources)},ensure_ascii=False))
