#!/bin/bash
set -e

pushd server/dotnet
node apply-version.js
dotnet build
dotnet test TinyBI.Engine.Tests/TinyBI.Engine.Tests.csproj
popd

pushd client
yarn
yarn prepare
popd

pushd server/dotnet
. ./push.sh
popd

pushd client
. ./publish.sh
popd
