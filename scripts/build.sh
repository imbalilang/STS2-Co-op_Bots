#!/bin/zsh
set -euo pipefail

script_dir="${0:A:h}"
repo_dir="${script_dir:h}"
dotnet_bin="${repo_dir}/work/.dotnet/dotnet"
game_data_default="/Users/xiwa/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
game_data="${STS2_DATA_DIR:-${game_data_default}}"
version="$(sed -n 's/.*"version": "\([^"]*\)".*/\1/p' "${repo_dir}/src/CoopBots/mod_manifest.json")"
baseline_output="${repo_dir}/work/build-macos-${version}"
release_dir="${repo_dir}/outputs/CoopBots-v${version}"

if [[ ! -x "${dotnet_bin}" ]]; then
  dotnet_bin="$(command -v dotnet)"
fi
if [[ ! -f "${game_data}/sts2.dll" ]]; then
  print -u2 "找不到官方 sts2.dll：${game_data}"
  print -u2 "可设置 STS2_DATA_DIR 后重试。"
  exit 2
fi

mkdir -p "${baseline_output}"
"${dotnet_bin}" restore "${repo_dir}/src/CoopBots/CoopBots.csproj" --ignore-failed-sources \
  -p:STS2DataDir="${game_data}" -p:NuGetAudit=false
"${dotnet_bin}" build "${repo_dir}/src/CoopBots/CoopBots.csproj" -c Release --no-restore \
  -p:STS2DataDir="${game_data}" -p:NuGetAudit=false -p:OutputPath="${baseline_output}/"
# The game enumerates our types before the initializer can load CoopBots.Kernel.
# Guard against a type that needs the kernel at load time (see tests/TypeLoadCheck).
"${dotnet_bin}" run --project "${repo_dir}/tests/TypeLoadCheck/TypeLoadCheck.csproj" -c Release \
  -- "${baseline_output}" "${game_data}"
"${dotnet_bin}" restore "${repo_dir}/tests/PatchSmoke/PatchSmoke.csproj" --ignore-failed-sources \
  -p:CoopBotsDll="${baseline_output}/CoopBots.dll" -p:STS2DataDir="${game_data}"
"${dotnet_bin}" run --project "${repo_dir}/tests/PatchSmoke/PatchSmoke.csproj" -c Release --no-restore \
  -p:CoopBotsDll="${baseline_output}/CoopBots.dll" -p:STS2DataDir="${game_data}"

mkdir -p "${release_dir}/CoopBots"
cp "${baseline_output}/CoopBots.dll" "${release_dir}/CoopBots/"
cp "${repo_dir}/src/CoopBots/mod_manifest.json" "${release_dir}/CoopBots/"
cp "${repo_dir}/README.md" "${release_dir}/README.md"
cp "${repo_dir}/CHANGELOG.md" "${release_dir}/CHANGELOG.md"

rm -f "${release_dir}.zip"
ditto -c -k --norsrc --keepParent "${release_dir}/CoopBots" "${release_dir}.zip"
print "发布包已生成：${release_dir}"
