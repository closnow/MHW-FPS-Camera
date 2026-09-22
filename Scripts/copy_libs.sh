#! /usr/bin/env sh

: ${1?"Usage: $0 <path_to_repos>"}

BASE_PATH="$1"
GAME_PATH="/mnt/ssd/SteamLibrary/steamapps/common/Monster Hunter World/"

BUILD_TYPE="Release"

rsync -av "$BASE_PATH/SharpPluginLoader-fork/x64/$BUILD_TYPE/mhw-cs-plugin-loader.dll" "$GAME_PATH/ucrtbase.dll"
#rsync -av "$BASE_PATH/SharpPluginLoader-fork/x64/$BUILD_TYPE/mhw-cs-plugin-loader.dll" "$GAME_PATH/winmm.dll"
#rsync -av "$BASE_PATH/SharpPluginLoader-fork/x64/$BUILD_TYPE/SPLLauncher.exe" "$GAME_PATH/SPLLauncher.exe"
rsync -av "$BASE_PATH/SharpPluginLoader-fork/SharpPluginLoader.Core/bin/$BUILD_TYPE/net8.0/SharpPluginLoader.Core.dll" "$GAME_PATH/nativePC/plugins/CSharp/Loader/"
rsync -av "$BASE_PATH/SharpPluginLoader-fork/SharpPluginLoader.Bootstrapper/bin/$BUILD_TYPE/net8.0/SharpPluginLoader.Bootstrapper.dll" "$GAME_PATH/nativePC/plugins/CSharp/Loader/"
rsync -av "$BASE_PATH/SharpPluginLoader-fork/Assets/Default.bin" "$GAME_PATH/nativePC/plugins/CSharp/Loader/"

rsync -av "$BASE_PATH/NewCamera/bin/Release/net8.0/NewCamera.dll" "$GAME_PATH/nativePC/plugins/CSharp/"
rsync -av "$BASE_PATH/WorldTuningTool/bin/Release/net8.0/WorldTuningTool.dll" "$GAME_PATH/nativePC/plugins/CSharp/"
