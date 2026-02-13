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
    setConfig('desktop_api_url', url);
    setConfig('desktop_last_seen', new Date().toISOString());
    if (hostname) setConfig('desktop_hostname', hostname);

    console.log(`Desktop registered: ${url}`);
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

    for (const [key, value] of Object.entries(updates)) {
        setConfig(key, String(value));
    }

    res.json({ updated: true, config: getAllConfig() });
});

app.get('/panel/connection', authMiddleware, (req, res) => {
    const url = getConfig('desktop_api_url', 'not configured');
    const lastSeen = getConfig('desktop_last_seen', 'never');
    const hostname = getConfig('desktop_hostname', 'unknown');
    const connected = streamProxy.isDesktopConnected();
    res.json({ url, lastSeen, hostname, connected });
});

// API proxy routes (require auth, resolve desktop URL dynamically)
app.use('/api', authMiddleware, createApiProxy());

// Push-based stream relay: desktop connects OUT to VPS
const streamProxy = setupStreamProxy(io, server);

// Socket.IO for real-time updates — uses pushed status from desktop
io.on('connection', (socket) => {
    console.log(`Client connected: ${socket.id}`);

    // Send status from desktop push every 2s
    const statusInterval = setInterval(() => {
        const status = streamProxy.getLastStatus();
        socket.emit('status', status);
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
    console.log(`Shot Clipper Panel running on port ${PORT}`);
    console.log(`Desktop connects via WebSocket to /desktop-push`);
});
