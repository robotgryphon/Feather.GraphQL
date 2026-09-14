#!/bin/bash
# Setup script for Claude Code cloud sessions (claude.ai/code, mobile app, `claude --cloud`).
#
# This file is NOT run by anything automatically. Paste its contents into the "Setup script"
# field of the cloud environment at claude.ai/code. It is kept in the repository so the
# environment's configuration is reviewable and does not live only in a web form.
# See docs/cloud-sessions.md.
#
# Runs as root on Ubuntu 24.04, before Claude Code launches, once per environment cache
# (~7 days, or whenever this script changes). A non-zero exit stops the session from starting.

echo "Installing the .NET SDK..."

# Preferred: the same channel CI resolves, so a cloud session and a CI run use the same SDK
# feature band. Needs aka.ms and builds.dotnet.microsoft.com, which the Trusted network level
# does not allow — it works only if the environment adds them under Custom access.
if curl -fsSL --max-time 60 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
  && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet --no-path; then
  echo "Installed the SDK from the .NET CDN."
else
  # Fallback: Ubuntu's own archive, which is on the Trusted allowlist. Its 10.0 SDK sits in a
  # lower feature band than the CDN's latest, so treat a failure that reproduces only here as
  # suspect until it is confirmed against CI.
  echo "The .NET CDN was unreachable; installing from the Ubuntu archive instead."
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y dotnet-sdk-10.0 || exit 1
fi

# Put dotnet on PATH for every shell Claude opens, whichever branch installed it.
if [ -x /usr/share/dotnet/dotnet ]; then
  ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet
  cat > /etc/profile.d/dotnet.sh <<'PROFILE'
export DOTNET_ROOT=/usr/share/dotnet
export PATH="$PATH:/usr/share/dotnet:$HOME/.dotnet/tools"
PROFILE
fi

# Native AOT publishes the linux-x64 smoke sample, which needs a native toolchain and zlib.
# Best-effort: the build and the tests do not depend on it, so a failure here must not stop
# the session from starting.
DEBIAN_FRONTEND=noninteractive apt-get install -y clang zlib1g-dev || true

dotnet --info || exit 1

exit 0
