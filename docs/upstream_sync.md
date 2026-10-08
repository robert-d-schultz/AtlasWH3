# Looking at upstream (Atlas3K)

AtlasWH3 is a hard fork of [Atlas3K](https://github.com/Ironictw2st/Atlas3K). It does not merge upstream, but fixes in
the code both share (packs, PNG and zlib writers, compressed_map, hex grid, Terry XML, the editors' plumbing) are
worth cherry-picking.

The `upstream` remote is fetch-only (`git remote set-url --push upstream no_push`). The tag `fork-point` marks the
commit AtlasWH3 started from (Atlas3K `cdd0a08`, 0.1.0-alpha.2).

## What changed upstream since the fork

```
git fetch upstream
git log --oneline fork-point..upstream/master
git log --stat fork-point..upstream/master -- src/Atlas3K.Formats   # only one area
```

## Cherry-picking a fix

AtlasWH3 renamed every project, folder and namespace (`Atlas3K` → `AtlasWH3`, `src/Atlas3K.*` → `src/AtlasWH3.*`) in
one mechanical commit, so an upstream patch does not apply as is. Rewrite the paths and names first:

```
git format-patch -1 <upstream commit> --stdout \
  | sed 's#src/Atlas3K\.#src/AtlasWH3.#g; s/Atlas3K/AtlasWH3/g; s/atlas3k/atlaswh3/g; s/ATLAS3K/ATLASWH3/g' \
  | git am -3
```

Expect conflicts in code AtlasWH3 cut or re-ported for WH3:
- cut: the battle terrain editor, global meshes, river height patches, lf height maps, `climate_map.cm`, seasons;
- re-ported: the build steps (tile list, trees, props, rivers, camera height map, hlp/spd), the DB tables, the
  Terry map types.

A fix to one of those usually has to be redone by hand on the WH3 version, if it applies at all.

## Keeping a record

Name the upstream commit in the AtlasWH3 commit message (`cherry-picked from Atlas3K <hash>`), so
`git log --grep "Atlas3K "` lists what has been taken.
