#!/usr/bin/env bash
source "$(dirname "${BASH_SOURCE[0]}")/env.sh"
dotnet test tests/W.Dameng.Identity.Tests "${identity_build_flags[@]}" "$@"
