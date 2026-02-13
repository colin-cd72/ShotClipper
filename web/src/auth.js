const express = require('express');
const bcrypt = require('bcryptjs');
const jwt = require('jsonwebtoken');
const { getDb } = require('./db');

const router = express.Router();
const JWT_SECRET = process.env.JWT_SECRET || 'screener-panel-secret';
const TOKEN_EXPIRY = '7d';

// Register
router.post('/register', async (req, res) => {
    try {
        const { username, password, role } = req.body;

        if (!username || !password) {
            return res.status(400).json({ error: 'Username and password required' });
        }

        const db = getDb();

        // Check if first user (auto-admin)
        const userCount = db.prepare('SELECT COUNT(*) as count FROM users').get();
        const assignedRole = userCount.count === 0 ? 'admin' : (role || 'viewer');

        // Only admins can create admin users after the first
        if (assignedRole === 'admin' && userCount.count > 0) {
            // Verify requester is admin
            const authHeader = req.headers.authorization;
            if (!authHeader) {
                return res.status(403).json({ error: 'Admin access required to create admin users' });
            }

            try {
                const token = authHeader.replace('Bearer ', '');
                const decoded = jwt.verify(token, JWT_SECRET);
                if (decoded.role !== 'admin') {
                    return res.status(403).json({ error: 'Admin access required' });
                }
            } catch {
                return res.status(403).json({ error: 'Invalid token' });
            }
        }

        const passwordHash = await bcrypt.hash(password, 10);

        db.prepare('INSERT INTO users (username, password_hash, role) VALUES (?, ?, ?)')
            .run(username, passwordHash, assignedRole);

        res.status(201).json({ message: 'User created', role: assignedRole });
    } catch (err) {
        if (err.message?.includes('UNIQUE')) {
            return res.status(409).json({ error: 'Username already exists' });
        }
        res.status(500).json({ error: err.message });
    }
});

// Login
router.post('/login', async (req, res) => {
    try {
        const { username, password } = req.body;

        if (!username || !password) {
            return res.status(400).json({ error: 'Username and password required' });
        }

        const db = getDb();
        const user = db.prepare('SELECT * FROM users WHERE username = ?').get(username);

        if (!user) {
            return res.status(401).json({ error: 'Invalid credentials' });
        }

        const valid = await bcrypt.compare(password, user.password_hash);
        if (!valid) {
            return res.status(401).json({ error: 'Invalid credentials' });
        }

        // Update last login
        db.prepare('UPDATE users SET last_login = datetime(\'now\') WHERE id = ?').run(user.id);

        const token = jwt.sign(
            { id: user.id, username: user.username, role: user.role },
            JWT_SECRET,
            { expiresIn: TOKEN_EXPIRY }
        );

        res.json({ token, user: { id: user.id, username: user.username, role: user.role } });
    } catch (err) {
        res.status(500).json({ error: err.message });
    }
});

// Get current user
router.get('/me', authMiddleware, (req, res) => {
    res.json({ user: req.user });
});

// Auth middleware
function authMiddleware(req, res, next) {
    const authHeader = req.headers.authorization;
    const cookieToken = req.cookies?.token;
    const token = authHeader?.replace('Bearer ', '') || cookieToken;

    if (!token) {
        return res.status(401).json({ error: 'Authentication required' });
    }

    try {
        const decoded = jwt.verify(token, JWT_SECRET);
        req.user = decoded;
        next();
    } catch {
        return res.status(401).json({ error: 'Invalid or expired token' });
    }
}

// Optional auth (for public endpoints)
function optionalAuth(req, res, next) {
    const authHeader = req.headers.authorization;
    const cookieToken = req.cookies?.token;
    const token = authHeader?.replace('Bearer ', '') || cookieToken;

    if (token) {
        try {
            req.user = jwt.verify(token, JWT_SECRET);
        } catch {
            // Ignore invalid tokens for optional auth
        }
    }
    next();
}

// Admin-only middleware
function adminOnly(req, res, next) {
    if (req.user?.role !== 'admin') {
        return res.status(403).json({ error: 'Admin access required' });
    }
    next();
}

module.exports = { authRouter: router, authMiddleware, optionalAuth, adminOnly };
