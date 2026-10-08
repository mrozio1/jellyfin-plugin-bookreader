"""Adds the just-built release to manifest.json (Jellyfin's plugin repository format)."""
import datetime
import json
import os
import pathlib

path = pathlib.Path("manifest.json")
manifest = json.loads(path.read_text())
plugin = manifest[0]

version = os.environ["VERSION"]
entry = {
    "version": version,
    "changelog": f"Release {version}",
    "targetAbi": "10.11.0.0",
    "sourceUrl": os.environ["URL"],
    "checksum": os.environ["MD5"],
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}

plugin["versions"] = [v for v in plugin.get("versions", []) if v.get("version") != version]
plugin["versions"].insert(0, entry)
path.write_text(json.dumps(manifest, indent=2) + "\n")
print(f"manifest.json now lists {len(plugin['versions'])} version(s)")
