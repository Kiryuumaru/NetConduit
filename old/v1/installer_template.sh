#!/bin/bash

username="{{$username}}"
repo="{{$repo}}"
appname="{{$appname}}"
appexec="{{$appexec}}"
rootextract="{{$rootextract}}"

root="$TMPDIR/$username/$repo/$appname"
tempPath="$root/temp"

mkdir -p "$tempPath"

appZipName="$appname.zip"
appPath="$tempPath/$appname"
appZipPath="$tempPath/$appZipName"
appExecPath="$appPath/$appexec"

if [[ -f "$appZipPath" ]]; then
    rm -f "$appZipPath"
fi

appUri="https://github.com/$username/$repo/releases/latest/download/$appZipName"
curl -L "$appUri" -o "$appZipPath"

unzip -o "$appZipPath" -d "$tempPath"

"$appExecPath" update

HOME_PATH="{{$homepath}}"
if [[ ":$PATH:" != *":$HOME_PATH:"* ]]; then
    export PATH="$PATH:$HOME_PATH:$HOME_PATH"
    echo "export PATH=\$PATH:$HOME_PATH:$HOME_PATH" >> ~/.bashrc
fi
