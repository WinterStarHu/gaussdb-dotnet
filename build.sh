#!/bin/sh
set -eu

target="Default"
framework=""
api_key=""
no_push="false"
stable="false"

for arg in "$@"; do
  case "$arg" in
    --target=*)
      target="${arg#*=}"
      ;;
    --framework=*)
      framework="${arg#*=}"
      ;;
    --apiKey=*)
      api_key="${arg#*=}"
      ;;
    --noPush|--noPush=true)
      no_push="true"
      ;;
    --stable|--stable=true)
      stable="true"
      ;;
  esac
done

repo_root="$(cd "$(dirname "$0")" && pwd)"
local_dotnet="$repo_root/.dotnet/dotnet"
local_dotnet_win="$repo_root/.dotnet/dotnet.exe"
use_local_dotnet="false"

if [ -x "$local_dotnet" ]; then
  dotnet_exe="$local_dotnet"
  use_local_dotnet="true"
elif [ -f "$local_dotnet_win" ]; then
  dotnet_exe="$local_dotnet_win"
  use_local_dotnet="true"
elif command -v dotnet >/dev/null 2>&1; then
  dotnet_exe="$(command -v dotnet)"
else
  echo "dotnet not found. Checked $local_dotnet, $local_dotnet_win, and PATH." >&2
  exit 1
fi

if [ "$use_local_dotnet" = "true" ]; then
  export DOTNET_ROOT="$repo_root/.dotnet"
fi
export DOTNET_MULTILEVEL_LOOKUP=0
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_NOLOGO=1
export MSBuildEnableWorkloadResolver=false
if [ -z "${DOTNET_CLI_HOME:-}" ]; then
  export DOTNET_CLI_HOME="$repo_root/.dotnet-cli-home"
fi

solution_path="$repo_root/GaussDB.slnx"
package_output="$repo_root/artifacts/packages"

build_projects='
./src/GaussDB.SourceGenerators/GaussDB.SourceGenerators.csproj|false|false
./src/GaussDB/GaussDB.csproj|true|true
./src/GaussDB.DependencyInjection/GaussDB.DependencyInjection.csproj|true|true
./src/GaussDB.GeoJSON/GaussDB.GeoJSON.csproj|true|true
./src/GaussDB.Json.NET/GaussDB.Json.NET.csproj|true|true
./src/GaussDB.NetTopologySuite/GaussDB.NetTopologySuite.csproj|true|true
./src/GaussDB.NodaTime/GaussDB.NodaTime.csproj|true|true
./src/GaussDB.OpenTelemetry/GaussDB.OpenTelemetry.csproj|true|true
./example/GetStarted/GetStarted.csproj|true|true
./test/GaussDB.Benchmarks/GaussDB.Benchmarks.csproj|true|true
./test/GaussDB.NativeAotTests/GaussDB.NativeAotTests.csproj|true|true
./test/GaussDB.Specification.Tests/GaussDB.Specification.Tests.csproj|true|true
./test/GaussDB.Tests/GaussDB.Tests.csproj|true|true
./test/GaussDB.DependencyInjection.Tests/GaussDB.DependencyInjection.Tests.csproj|true|true
./test/GaussDB.PluginTests/GaussDB.PluginTests.csproj|true|true
'

test_projects='
./test/GaussDB.Tests/GaussDB.Tests.csproj
./test/GaussDB.DependencyInjection.Tests/GaussDB.DependencyInjection.Tests.csproj
'

pack_projects='
./src/GaussDB/GaussDB.csproj
'

common_build_args='-m:1 -p:NuGetAudit=false'

write_task_banner() {
  name="$1"
  description="$2"
  state="$3"
  echo "===== Task [$name] $description $state ======"
}

run_dotnet() {
  echo "Executing command:"
  printf '    %s' "$dotnet_exe"
  for arg in "$@"; do
    case "$arg" in
      *" "*|*";"*|*"\""*)
        printf ' "%s"' "$(printf '%s' "$arg" | sed 's/"/\\"/g')"
        ;;
      *)
        printf ' %s' "$arg"
        ;;
    esac
  done
  printf '\n\n'

  "$dotnet_exe" "$@"
  printf '\n'
}

invoke_project_build() {
  project_path="$1"
  use_framework="$2"
  no_dependencies="$3"

  set -- build "$project_path"
  if [ "$use_framework" = "true" ] && [ -n "$framework" ]; then
    set -- "$@" -f "$framework"
  fi
  if [ "$no_dependencies" = "true" ]; then
    set -- "$@" --no-dependencies
  fi
  for arg in $common_build_args; do
    set -- "$@" "$arg"
  done

  if run_dotnet "$@"; then
    return
  fi

  if [ "$use_framework" != "true" ] || [ -z "$framework" ]; then
    return 1
  fi

  echo "Retrying build without restore for $project_path ($framework)..."
  set -- build "$project_path" -f "$framework" --no-restore
  if [ "$no_dependencies" = "true" ]; then
    set -- "$@" --no-dependencies
  fi
  for arg in $common_build_args; do
    set -- "$@" "$arg"
  done
  run_dotnet "$@"
}

get_build_projects() {
  printf '%s' "$build_projects" | while IFS='|' read -r project_path use_framework no_dependencies; do
    [ -n "$project_path" ] || continue

    if [ "$framework" = "net8.0" ] && [ "$project_path" = "./test/GaussDB.NativeAotTests/GaussDB.NativeAotTests.csproj" ]; then
      continue
    fi

    printf '%s|%s|%s\n' "$project_path" "$use_framework" "$no_dependencies"
  done
}

invoke_build() {
  write_task_banner "build" "build" "executing"

  if [ -z "$framework" ]; then
    set -- build "$solution_path"
    for arg in $common_build_args; do
      set -- "$@" "$arg"
    done
    run_dotnet "$@"
    write_task_banner "build" "build" "executed"
    return
  fi

  get_build_projects | while IFS='|' read -r project_path use_framework no_dependencies; do
    invoke_project_build "$project_path" "$use_framework" "$no_dependencies"
  done

  write_task_banner "build" "build" "executed"
}

invoke_test() {
  write_task_banner "test" "dotnet test" "executing"
  invoke_build

  for project in $test_projects; do
    if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
      logger="GitHubActions"
    else
      logger="console;verbosity=d"
    fi

    set -- test --blame --collect "XPlat Code Coverage;Format=cobertura,opencover;ExcludeByAttribute=ExcludeFromCodeCoverage,Obsolete,GeneratedCode,CompilerGenerated" --logger "$logger" -v:d
    if [ -n "$framework" ]; then
      set -- "$@" -f "$framework"
    fi
    for arg in $common_build_args; do
      set -- "$@" "$arg"
    done
    set -- "$@" "$project"
    run_dotnet "$@"
  done

  write_task_banner "test" "dotnet test" "executed"
}

invoke_pack() {
  write_task_banner "pack" "dotnet pack" "executing"
  invoke_build

  rm -rf "$package_output"
  mkdir -p "$package_output"

  for project in $pack_projects; do
    set -- pack "$project" -o "$package_output"
    if [ "$stable" = "true" ] || [ -n "${VERSION:-}" ]; then
      if [ -n "${VERSION:-}" ]; then
        set -- "$@" -p "VersionPrefix=$VERSION"
      fi
    else
      suffix="preview-$(date -u +%Y%m%d-%H%M%S)"
      set -- "$@" --version-suffix "$suffix"
    fi
    for arg in $common_build_args; do
      set -- "$@" "$arg"
    done
    run_dotnet "$@"
  done

  if [ "$no_push" = "true" ]; then
    echo "Skip push there's noPush specified"
    write_task_banner "pack" "dotnet pack" "executed"
    return
  fi

  if [ -z "$api_key" ] && [ -n "${NUGET_API_KEY:-}" ]; then
    api_key="$NUGET_API_KEY"
  fi

  if [ -z "$api_key" ]; then
    echo "Skip push since there's no apiKey found"
    write_task_banner "pack" "dotnet pack" "executed"
    return
  fi

  for package in "$package_output"/*.nupkg; do
    [ -e "$package" ] || continue
    run_dotnet nuget push "$package" -s "https://api.nuget.org/v3/index.json" -k "$api_key" --skip-duplicate
  done

  write_task_banner "pack" "dotnet pack" "executed"
}

case "$target" in
  build)
    invoke_build
    ;;
  test)
    invoke_test
    ;;
  pack|Default)
    invoke_pack
    ;;
  *)
    echo "Unknown target: $target" >&2
    exit 1
    ;;
esac
