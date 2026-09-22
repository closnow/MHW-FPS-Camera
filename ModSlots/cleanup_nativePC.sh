#! /usr/bin/env sh

if [[ $(basename "$PWD") != "nativePC" ]]; then
  echo "This should only be run from the 'Monster Hunter World/nativePC' directory."
  exit 1
fi

if [[ "$1" == "print" ]]; then
  find . -depth -type d -empty -print | grep .
else
  find . -depth -type d -empty -print -delete | grep .
fi

if [[ $? != 0 ]]; then
  echo "No empty directories found."
fi
