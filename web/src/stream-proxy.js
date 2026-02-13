const WebSocket = require('ws');
const { getConfig, setConfig } = require('./db');

/**
 * Push-based stream relay: desktop connects OUT to VPS and pushes frames + status.
 * No inbound connections to desktop needed — works through any firewall/NAT.
 */
function setupStreamProxy(io, server) {
    const streamNamespace = io.of('/stream');
    const wss = new WebSocket.Server({ noServer: true });
    let desktopSocket = null;
    let lastStatus = { _desktopOnline: false };

    // Handle WebSocket upgrade for /desktop-push
    server.on('upgrade', (req, socket, head) => {
        const url = new URL(req.url, 'http://localhost');
        if (url.pathname === '/desktop-push') {
            wss.handleUpgrade(req, socket, head, (ws) => {
                wss.emit('connection', ws, req);
            });
        }
    });

    wss.on('connection', (ws, req) => {
        let authenticated = false;
        console.log('Desktop push connection attempt');

        // 10s auth timeout
        const authTimeout = setTimeout(() => {
            if (!authenticated) {
                console.log('Desktop push auth timeout');
                ws.close();
            }
        }, 10000);

        ws.on('message', (data, isBinary) => {
            if (!authenticated) {
                // First message must be JSON auth
                clearTimeout(authTimeout);
                try {
                    const msg = JSON.parse(data.toString());
                    const storedKey = getConfig('desktop_api_key', '');

                    if (msg.type === 'auth' && storedKey && msg.apiKey === storedKey) {
                        authenticated = true;
                        desktopSocket = ws;
                        setConfig('desktop_last_seen', new Date().toISOString());
                        if (msg.hostname) setConfig('desktop_hostname', msg.hostname);
                        console.log(`Desktop authenticated (hostname: ${msg.hostname || 'unknown'})`);
                        ws.send(JSON.stringify({ type: 'auth_ok' }));
                        return;
                    }
                } catch {}

                console.log('Desktop push auth failed');
                ws.send(JSON.stringify({ type: 'auth_fail' }));
                ws.close();
                return;
            }

            // Authenticated — handle frames and status
            if (isBinary) {
                // JPEG frame → relay to all browser viewers
                streamNamespace.emit('frame', data);
            } else {
                // JSON status update
                try {
                    const status = JSON.parse(data.toString());
                    if (status.type === 'status') {
                        delete status.type;
                        status._desktopOnline = true;
                        lastStatus = status;
                    }
                } catch {}
            }

            // Update last seen periodically
            setConfig('desktop_last_seen', new Date().toISOString());
        });

        ws.on('close', () => {
            clearTimeout(authTimeout);
            if (desktopSocket === ws) {
                desktopSocket = null;
                lastStatus = { _desktopOnline: false };
                console.log('Desktop push disconnected');
            }
        });

        ws.on('error', (err) => {
            console.error('Desktop push error:', err.message);
        });
    });

    // Browser viewers connect via Socket.IO /stream namespace
    streamNamespace.on('connection', (socket) => {
        console.log(`Stream viewer connected: ${socket.id}`);
        socket.on('disconnect', () => {
            console.log(`Stream viewer disconnected: ${socket.id}`);
        });
    });

    return {
        getLastStatus: () => lastStatus,
        isDesktopConnected: () => desktopSocket !== null && desktopSocket.readyState === WebSocket.OPEN
    };
}

module.exports = { setupStreamProxy };
