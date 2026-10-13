#!/bin/sh
# Builds Coopfall for NeoModLoader and packs release/Coopfall_NML.zip (+ .sha256).
#   sh client/build-nml.sh                      (game in the default Steam folder)
#   MANAGED=/path/to/worldbox_Data/Managed sh client/build-nml.sh
# NeoModLoader.dll is only needed to compile; it is downloaded to client/lib (git-ignored).
set -e
cd "$(dirname "$0")/.."
LIB=client/lib/NeoModLoader.dll
[ -f "$LIB" ] || curl -sSL -o "$LIB" https://github.com/WorldBoxOpenMods/ModLoader/releases/download/latest/NeoModLoader.dll
OUT=$(mktemp -d)
if [ -n "$MANAGED" ]; then
  dotnet build client/CoopfallNml/CoopfallNml.csproj -c Release -p:ManagedDir="$MANAGED" -o "$OUT/bin"
else
  dotnet build client/CoopfallNml/CoopfallNml.csproj -c Release -o "$OUT/bin"
fi
mkdir -p "$OUT/pkg/Coopfall"
cp "$OUT/bin/Coopfall.dll" client/CoopfallNml/mod.json client/CoopfallNml/icon.png LICENSE THIRD-PARTY-NOTICES.md "$OUT/pkg/Coopfall/"
cp client/CoopfallNml/HOW-TO-INSTALL.txt "$OUT/pkg/"
rm -f release/Coopfall_NML.zip
(cd "$OUT/pkg" && python3 -c "
import zipfile,os
with zipfile.ZipFile('nml.zip','w',zipfile.ZIP_DEFLATED) as z:
    for root,_,files in os.walk('.'):
        for f in sorted(files):
            if f!='nml.zip': p=os.path.join(root,f)[2:]; z.write(p,p.replace(os.sep,'/'))
")
cp "$OUT/pkg/nml.zip" release/Coopfall_NML.zip
sha256sum release/Coopfall_NML.zip | cut -d' ' -f1 > release/Coopfall_NML.zip.sha256
rm -rf "$OUT"
echo "release/Coopfall_NML.zip $(cat release/Coopfall_NML.zip.sha256)"
