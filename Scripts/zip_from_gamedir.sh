#! /usr/bin/env sh

7z a -tzip "NewCamera+WorldTuningTool_$1_incl_spl_linux.zip" \
  ucrtbase.dll \
  nativePC/plugins/CSharp/Loader/Default.bin \
  nativePC/plugins/CSharp/Loader/SharpPluginLoader.Bootstrapper.dll \
  nativePC/plugins/CSharp/Loader/SharpPluginLoader.Core.dll \
  nativePC/plugins/CSharp/Loader/SharpPluginLoader.runtimeconfig.json \
  nativePC/plugins/CSharp/NewCamera.dll \
  nativePC/plugins/CSharp/NewCamera.json \
  nativePC/plugins/CSharp/Shaders/* \
  nativePC/plugins/CSharp/LUT/* \
  nativePC/plugins/CSharp/WorldTuningTool.dll \
  nativePC/plugins/CSharp/WorldTuningTool.json
