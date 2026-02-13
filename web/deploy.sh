#!/bin/bash
# Deploy ShotClipper web panel to CloudPanel VPS
# Run this on the VPS after git pull

set -e

DEPLOY_DIR="/home/shotclipper/htdocs/shotclipper.4tmrw.net"

echo "=== ShotClipper Panel Deployment ==="

# 1. Navigate to deployment directory
cd "$DEPLOY_DIR"

echo "[1/5] Installing dependencies..."
npm install --production

# 2. Create .env if it doesn't exist
if [ ! -f .env ]; then
    echo "[2/5] Creating .env from template..."
    cp .env.example .env
    # Generate a random JWT secret
    JWT_SECRET=$(openssl rand -hex 32)
    sed -i "s/change-this-to-a-random-secret/$JWT_SECRET/" .env
    echo "  -> .env created. Edit it to set SCREENER_API_URL and SCREENER_API_KEY"
    echo "  -> Or configure via the web panel Settings page after first login"
else
    echo "[2/5] .env already exists, skipping"
fi

# 3. Install PM2 globally if not present
if ! command -v pm2 &> /dev/null; then
    echo "[3/5] Installing PM2..."
    npm install -g pm2
else
    echo "[3/5] PM2 already installed"
fi

# 4. Start/restart with PM2
echo "[4/5] Starting application with PM2..."
if pm2 describe screener-panel > /dev/null 2>&1; then
    pm2 restart screener-panel
    echo "  -> Restarted existing process"
else
    pm2 start ecosystem.config.js
    pm2 save
    echo "  -> Started new process"
fi

# 5. Setup PM2 startup (run on first deploy)
echo "[5/5] Ensuring PM2 startup..."
pm2 startup systemd -u shotclipper --hp /home/shotclipper 2>/dev/null || true
pm2 save

echo ""
echo "=== Deployment complete ==="
echo "Panel URL: https://shotclipper.4tmrw.net"
echo "PM2 status: pm2 status"
echo "PM2 logs:   pm2 logs screener-panel"
echo ""
echo "First time? Register an admin account at the login page."
echo "Then go to Settings > Desktop Connection to configure the desktop app URL."
