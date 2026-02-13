const WebSocket = require('ws');
const { getConfig } = require('./db');

/**
 * Relays the MJPEG WebSocket stream from the desktop app to browser clients.
 * Resolves desktop URL dynamically from config DB.
 */
function setupStreamProxy(io) {
    const streamNamespace = io.of('/stream');
    let desktopWs = null;
    let reconnectTimer = null;
    let currentWsUrl = null;

    function getDesktopWsUrl() {
        const apiUrl = getConfig('desktop_api_url', process.env.SCREENER_API_URL || 'http://localhost:8080');
        return apiUrl.replace(/^http/, 'ws') + '/ws';
    }

    function connectToDesktop() {
        const wsUrl = getDesktopWsUrl();

        // If URL changed, disconnect old connection
        if (desktopWs && currentWsUrl !== wsUrl) {
            desktopWs.close();
            desktopWs = null;
        }

        if (desktopWs && desktopWs.readyState === WebSocket.OPEN) return;

        currentWsUrl = wsUrl;

        try {
            desktopWs = new WebSocket(wsUrl);
            desktopWs.binaryType = 'arraybuffer';

            desktopWs.on('open', () => {
                console.log(`Connected to desktop stream at ${wsUrl}`);
            });

            desktopWs.on('message', (data) => {
                if (data instanceof ArrayBuffer || Buffer.isBuffer(data)) {
                    streamNamespace.emit('frame', data);
                } else {
                    try {
                        const msg = JSON.parse(data.toString());
                        streamNamespace.emit('signal', msg);
                    } catch {}
                }
            });

            desktopWs.on('close', () => {
                console.log('Desktop stream disconnected');
                desktopWs = null;
                scheduleReconnect();
            });

            desktopWs.on('error', (err) => {
                console.error('Desktop stream error:', err.message);
                desktopWs = null;
                scheduleReconnect();
            });
        } catch (err) {
            console.error('Failed to connect to desktop stream:', err.message);
            scheduleReconnect();
        }
    }

    function scheduleReconnect() {
        if (reconnectTimer) return;
        reconnectTimer = setTimeout(() => {
            reconnectTimer = null;
            if (streamNamespace.sockets.size > 0) {
                connectToDesktop();
            }
        }, 5000);
    }

    // Expose a function to force reconnect (called when desktop URL changes)
    function forceReconnect() {
        if (desktopWs) {
            desktopWs.close();
            desktopWs = null;
        }
        if (streamNamespace.sockets.size > 0) {
            connectToDesktop();
        }
    }

    streamNamespace.on('connection', (socket) => {
        console.log(`Stream viewer connected: ${socket.id}`);
        connectToDesktop();

        socket.on('disconnect', () => {
            console.log(`Stream viewer disconnected: ${socket.id}`);
            if (streamNamespace.sockets.size === 0 && desktopWs) {
                desktopWs.close();
                desktopWs = null;
            }
        });
    });

    return { forceReconnect };
}

module.exports = { setupStreamProxy };
