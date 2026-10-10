"""Extracts every campaign_maps/<map>/ AI pathfinding input and output (hlp_data.esf, spd_data.esf,
pathfinding.ppd, map_data.esf) from the CA packs and the user's mod packs, through rpfm_server.

usage: python extract_refs.py <out dir>     -> <out>/<source>/campaign_maps/<map>/...
"""
import sys
from pathlib import Path

sys.path.insert(0, r"C:\Users\rob\CascadeProjects\campaign_tools")
from shared.rpfm_client import Rpfm  # noqa: E402

DATA = Path(r"D:\SteamLibrary\steamapps\common\Total War WARHAMMER III\data")
MODS = {"iee": "!cr_immortal_empires_expanded.pack", "oldworld": "!cr_oldworld_campaign.pack",
        "oldworld_classic": "!cr_oldworld_classic_campaign.pack", "oldworld_darklands": "!cr_oldworld_darklands_campaign.pack"}
NAMES = ("hlp_data.esf", "spd_data.esf", "pathfinding.ppd", "map_data.esf")


def is_lookup(name):
    return name.endswith("_lookup.tga")


def wanted(paths):
    return sorted(p for p in paths if p.lower().startswith("campaign_maps/") and (p.rsplit("/", 1)[-1].lower() in NAMES or is_lookup(p.lower())))


def main(out):
    out = Path(out)
    with Rpfm() as rpfm:
        rpfm.call({"SetGameSelected": ["warhammer_3", False]})
        sources = []
        rpfm.call("LoadAllCAPackFiles")
        sources.append(("vanilla", "CA PackFiles", False))
        for name, pack in MODS.items():
            if (DATA / pack).exists():
                sources.append((name, rpfm.call({"OpenPackFiles": [str(DATA / pack)]})["StringContainerInfo"][0], True))
        for name, key, close in sources:
            files = wanted(f["path"] for f in rpfm.call({"GetPackFileDataForTreeView": key})["ContainerInfoVecRFileInfo"][1])
            print(name, len(files))
            for f in files:
                print("  ", f)
            if files:
                dest = out / name
                dest.mkdir(parents=True, exist_ok=True)
                rpfm.call({"ExtractPackedFiles": [key, {"PackFile": [{"File": f} for f in files]}, str(dest), False]})
            if close:
                rpfm.call({"ClosePack": key})


if __name__ == "__main__":
    main(sys.argv[1])
