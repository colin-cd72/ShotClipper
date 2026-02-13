// Auth module - handles login, registration, token management

const Auth = {
    token: localStorage.getItem('token'),
    user: JSON.parse(localStorage.getItem('user') || 'null'),

    isLoggedIn() {
        return !!this.token;
    },

    getHeaders() {
        return {
            'Content-Type': 'application/json',
            'Authorization': `Bearer ${this.token}`
        };
    },

    async login(username, password) {
        const res = await fetch('/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ username, password })
        });

        if (!res.ok) {
            const data = await res.json();
            throw new Error(data.error || 'Login failed');
        }

        const data = await res.json();
        this.token = data.token;
        this.user = data.user;
        localStorage.setItem('token', data.token);
        localStorage.setItem('user', JSON.stringify(data.user));
        return data;
    },

    async register(username, password) {
        const res = await fetch('/auth/register', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ username, password })
        });

        if (!res.ok) {
            const data = await res.json();
            throw new Error(data.error || 'Registration failed');
        }

        return await res.json();
    },

    logout() {
        this.token = null;
        this.user = null;
        localStorage.removeItem('token');
        localStorage.removeItem('user');
        window.location.reload();
    },

    isAdmin() {
        return this.user?.role === 'admin';
    }
};

// API helper
async function api(path, options = {}) {
    const res = await fetch(`/api${path}`, {
        ...options,
        headers: {
            ...Auth.getHeaders(),
            ...options.headers
        }
    });

    if (res.status === 401) {
        Auth.logout();
        throw new Error('Session expired');
    }

    return res;
}
