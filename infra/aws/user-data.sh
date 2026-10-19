#!/usr/bin/env bash
# EC2 user data (Amazon Linux 2023, arm64). Installs docker + compose plugin and clones the repo. No credentials:
# the SDK inside the container reaches the instance role through IMDSv2 (hop limit 2 is set at launch).
set -euxo pipefail
dnf install -y docker git
systemctl enable --now docker
mkdir -p /usr/local/lib/docker/cli-plugins
curl -fsSL "https://github.com/docker/compose/releases/download/v2.29.7/docker-compose-linux-aarch64" \
  -o /usr/local/lib/docker/cli-plugins/docker-compose
chmod +x /usr/local/lib/docker/cli-plugins/docker-compose
git clone https://github.com/scholtz/biatec-chat.git /opt/biatec-chat
# Next steps are manual over SSM Session Manager (docs/deploy-aws.md): write /opt/biatec-chat/.env (secrets typed by the
# operator, never committed) and run: docker compose up -d --build
