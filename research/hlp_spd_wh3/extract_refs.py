"""Extracts every campaign_maps/<map>/ AI pathfinding input and output (hlp_data.esf, spd_data.esf,
pathfinding.ppd, map_data.esf) from the CA packs and the user's mod packs, through rpfm_server.

usage: python extract_refs.py <out dir>     -> <out>/<source>/campaign_maps/<map>/...
"""
import sys
from pathlib import Path

sys.path.insert(0, r"C:\Users\rob\CascadeProjects\campaign_tools")
from shared.db import CA_PACKS, extract, open_pack, pack_paths  # noqa: E402
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
        rpfm.call("session.set_game", {"game": "warhammer_3"})
        sources = [("vanilla", CA_PACKS, False)]
        for name, pack in MODS.items():
            if (DATA / pack).exists():
                sources.append((name, open_pack(rpfm, DATA / pack), True))
        for name, key, close in sources:
            files = wanted(pack_paths(rpfm, key, "campaign_maps/"))
            print(name, len(files))
            for f in files:
                print("  ", f)
            if files:
                dest = out / name
                dest.mkdir(parents=True, exist_ok=True)
                extract(rpfm, key, files, dest)
            if close:
                rpfm.call("pack.close", {"pack": key})

if __name__ == "__main__":
    main(sys.argv[1])
