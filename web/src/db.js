const Database = require('better-sqlite3');
const path = require('path');

let db;

function initDb() {
    const dbPath = path.join(__dirname, '..', 'panel.db');
    db = new Database(dbPath);

    db.pragma('journal_mode = WAL');

    db.exec(`
        CREATE TABLE IF NOT EXISTS users (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            username TEXT UNIQUE NOT NULL,
            password_hash TEXT NOT NULL,
            role TEXT NOT NULL DEFAULT 'viewer',
            created_at TEXT DEFAULT (datetime('now')),
            last_login TEXT
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY,
            user_id INTEGER NOT NULL,
            created_at TEXT DEFAULT (datetime('now')),
            expires_at TEXT NOT NULL,
            FOREIGN KEY (user_id) REFERENCES users(id)
        );

        CREATE TABLE IF NOT EXISTS config (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT DEFAULT (datetime('now'))
        );
    `);

    return db;
}

function getDb() {
    if (!db) initDb();
    return db;
}

function getConfig(key, defaultValue = null) {
    const db = getDb();
    const row = db.prepare('SELECT value FROM config WHERE key = ?').get(key);
    return row ? row.value : defaultValue;
}

function setConfig(key, value) {
    const db = getDb();
    db.prepare(
        `INSERT INTO config (key, value, updated_at) VALUES (?, ?, datetime('now'))
         ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = datetime('now')`
    ).run(key, value);
}

function getAllConfig() {
    const db = getDb();
    const rows = db.prepare('SELECT key, value FROM config').all();
    const config = {};
    for (const row of rows) {
        config[row.key] = row.value;
    }
    return config;
}

module.exports = { initDb, getDb, getConfig, setConfig, getAllConfig };
