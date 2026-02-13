require('dotenv').config();

const express = require('express');
const http = require('http');
const { Server } = require('socket.io');
const cookieParser = require('cookie-parser');
const cors = require('cors');
const path = require('path');

const { initDb, getConfig, setConfig, getAllConfig } = require('./src/db');
const { authRouter, authMiddleware, adminOnly } = require('./src/auth');
const { createApiProxy } = require('./src/api');
const { setupStreamProxy } = require('./src/stream-proxy');

const app = express();
const server = http.createServer(app);
const io = new Server(server, {
    cors: { origin: '*' }
});

const PORT = process.env.PORT || 5010;

// Initialize database
initDb();

// Seed default config from .env if not already set
if (!getConfig('desktop_api_url') && process.env.SCREENER_API_URL) {
    setConfig('desktop_api_url', process.env.SCREENER_API_URL);
}
if (!getConfig('desktop_api_key') && process.env.SCREENER_API_KEY) {
    setConfig('desktop_api_key', process.env.SCREENER_API_KEY);
}

// Middleware
app.use(cors());
app.use(express.json());
app.use(cookieParser());
app.use(express.static(path.join(__dirname, 'public')));

// Auth routes (login, register user accounts)
app.use('/auth', authRouter);

// --- Desktop heartbeat/register endpoint (no panel auth required, uses API key) ---
app.post('/register', (req, res) => {
    const { apiKey, ip, port, hostname } = req.body;

    // Validate API key matches what the panel has stored
    const storedKey = getConfig('desktop_api_key', process.env.SCREENER_API_KEY || '');
    if (storedKey && apiKey !== storedKey) {
        return res.status(401).json({ error: 'Invalid API key' });
    }

    if (!ip || !port) {
        return res.status(400).json({ error: 'ip and port required' });
    }

    const url = `http://${ip}:${port}`;
    const previousUrl = getConfig('desktop_api_url');

    setConfig('desktop_api_url', url);
    setConfig('desktop_last_seen', new Date().toISOString());
    if (hostname) setConfig('desktop_hostname', hostname);

    console.log(`Desktop registered: ${url} (was: ${previousUrl})`);

    // Force stream reconnect if URL changed
    if (previousUrl !== url && streamProxy) {
        streamProxy.forceReconnect();
    }

    res.json({ registered: true, url });
});

// --- Panel config endpoints (admin only) ---
app.get('/panel/config', authMiddleware, adminOnly, (req, res) => {
    const config = getAllConfig();
    res.json(config);
});

app.put('/panel/config', authMiddleware, adminOnly, (req, res) => {
    const updates = req.body;

    if (typeof updates !== 'object') {
        return res.status(400).json({ error: 'Expected object of key-value pairs' });
    }

    const previousUrl = getConfig('desktop_api_url');

    for (const [key, value] of Object.entries(updates)) {
        setConfig(key, String(value));
    }

    // Force stream reconnect if desktop URL changed
    if (updates.desktop_api_url && updates.desktop_api_url !== previousUrl && streamProxy) {
        streamProxy.forceReconnect();
    }

    res.json({ updated: true, config: getAllConfig() });
});

app.get('/panel/connection', authMiddleware, (req, res) => {
    const url = getConfig('desktop_api_url', 'not configured');
    const lastSeen = getConfig('desktop_last_seen', 'never');
    const hostname = getConfig('desktop_hostname', 'unknown');
    res.json({ url, lastSeen, hostname });
});

// API proxy routes (require auth, resolve desktop URL dynamically)
app.use('/api', authMiddleware, createApiProxy());

// WebSocket stream relay (resolves desktop URL dynamically)
const streamProxy = setupStreamProxy(io);

// Socket.IO for real-time updates
io.on('connection', (socket) => {
    console.log(`Client connected: ${socket.id}`);

    // Poll desktop app status and broadcast
    const statusInterval = setInterval(async () => {
        try {
            const desktopUrl = getConfig('desktop_api_url', 'http://localhost:8080');
            const apiKey = getConfig('desktop_api_key', '');

            const res = await fetch(`${desktopUrl}/api/status`, {
                headers: { 'X-API-Key': apiKey },
                signal: AbortSignal.timeout(5000)
            });
            if (res.ok) {
                const status = await res.json();
                status._desktopOnline = true;
                socket.emit('status', status);
            } else {
                socket.emit('status', { _desktopOnline: false });
            }
        } catch {
            socket.emit('status', { _desktopOnline: false });
        }
    }, 2000);

    socket.on('disconnect', () => {
        clearInterval(statusInterval);
        console.log(`Client disconnected: ${socket.id}`);
    });
});

// SPA fallback
app.get('*', (req, res) => {
    res.sendFile(path.join(__dirname, 'public', 'index.html'));
});

server.listen(PORT, () => {
    const desktopUrl = getConfig('desktop_api_url', 'not configured');
    console.log(`Screener Panel running on port ${PORT}`);
    console.log(`Desktop API: ${desktopUrl}`);
});
