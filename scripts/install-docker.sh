#!/usr/bin/env bash
# Docker Engine install for Ubuntu 24.04 (user runs; needs sudo password).
# Source: official Docker docs (https://docs.docker.com/engine/install/ubuntu/).
# After finishing, run: docs/dev/SETUP.md Step 4.
set -euo pipefail

sudo apt-get update
sudo apt-get install -y ca-certificates curl
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc

# Debian/Ubuntu version reusable: matches $VERSION_CODENAME (noble for Ubuntu 24.04)
echo \
  "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu \
  $(. /etc/os-release && echo "$VERSION_CODENAME") stable" | \
  sudo tee /etc/apt/sources.list.d/docker.list > /dev/null

sudo apt-get update
sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

# Run docker without sudo (docker group); takes effect after logout/login OR `newgrp docker`.
sudo usermod -aG docker "$USER"

echo
echo "✔ Docker installed. If 'docker ps' complains about permissions, log out and back in."
docker --version
docker compose version
