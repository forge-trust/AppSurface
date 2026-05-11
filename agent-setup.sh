#!/usr/bin/env bash
set -euo pipefail

SDK_VERSION=9.0.300
INSTALL_DIR="$HOME/.dotnet"

# toggle backend restore/install, defaults to on
BE_RESTORE="${BE_RESTORE:-1}"

# 1-4. install dotnet and restore solution if enabled
if [[ "$BE_RESTORE" != "0" ]]; then
  curl -sSL https://dot.net/v1/dotnet-install.sh \
       | bash /dev/stdin --quality ga --version "$SDK_VERSION" --install-dir "$INSTALL_DIR"
  # apply to this shell
  export DOTNET_ROOT="$INSTALL_DIR"
  export PATH="$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools"

  # sanity check and restore
  dotnet --info
  dotnet restore ForgeTrust.AppSurface.slnx
  dotnet build ForgeTrust.AppSurface.slnx

  # make it stick for every future shell
  echo "Exporting DOTNET PATHS: $INSTALL_DIR"
  echo "export DOTNET_ROOT=\"$INSTALL_DIR\"" >> "$HOME/.bashrc"
  echo "export PATH=\"\$PATH:\$DOTNET_ROOT:\$DOTNET_ROOT/tools\"" >> "$HOME/.bashrc"
fi

# restore yarn packages if requested
if [[ -n "${FE_RESTORE_PATTERNS:-}" ]]; then
  echo "Restoring yarn packages for patterns: $FE_RESTORE_PATTERNS"

  IFS=';' read -ra patterns <<< "$FE_RESTORE_PATTERNS"
  shopt -s nullglob
  for pat in "${patterns[@]}"; do
    pat=${pat%/}
    matched=0
    for dir in $pat; do
      matched=1
      if [[ -f "$dir/package.json" ]]; then
        echo "Installing packages in $dir"
        (cd "$dir" && yarn install --immutable)
      else
        echo "Skipping $dir: package.json not found"
      fi
    done
    if [[ $matched -eq 0 ]]; then
      echo "No directories matched: $pat"
    fi
  done
  shopt -u nullglob
fi

exit 0
