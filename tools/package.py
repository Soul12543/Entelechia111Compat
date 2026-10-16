"""Stage only the independently authored patch and its documentation."""
from pathlib import Path
import hashlib
import shutil
import zipfile

root = Path(__file__).resolve().parents[1]
stage = root / "dist" / "Entelechia111Compat"
content = root / "workshop" / "content"
stage.mkdir(parents=True, exist_ok=True)
content.mkdir(parents=True, exist_ok=True)
dll = root / "src/Entelechia111Compat/bin/Release/net9.0/Entelechia111Compat.dll"
if not dll.is_file():
    raise SystemExit("Build the Release project before packaging.")
for name, source in {
    "Entelechia111Compat.dll": dll,
    "Entelechia111Compat.json": root / "Entelechia111Compat.json",
    "LICENSE": root / "LICENSE",
    "README.md": root / "README.md",
    "docs/ATTRIBUTION.md": root / "docs/ATTRIBUTION.md",
    "docs/VALIDATION.md": root / "docs/VALIDATION.md",
}.items():
    (stage / name).parent.mkdir(parents=True, exist_ok=True)
    (content / name).parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, stage / name)
    shutil.copy2(source, content / name)
expected = {"Entelechia111Compat.dll", "Entelechia111Compat.json", "LICENSE", "README.md", "docs/ATTRIBUTION.md", "docs/VALIDATION.md"}
for folder in (stage, content):
    actual = {p.relative_to(folder).as_posix() for p in folder.rglob("*") if p.is_file()}
    if actual != expected:
        raise SystemExit(f"Unexpected package files in {folder}: {sorted(actual ^ expected)}")
archive = root / "dist/Entelechia111Compat-v1.0.0.zip"
with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as z:
    for path in sorted(p for p in stage.rglob("*") if p.is_file()):
        z.write(path, arcname=f"Entelechia111Compat/{path.relative_to(stage).as_posix()}")
print(f"{archive}\nSHA256 {hashlib.sha256(archive.read_bytes()).hexdigest()}")
