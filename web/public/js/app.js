// Main SPA router and navigation

const App = {
    currentPage: null,
    socket: null,

    pages: {
        dashboard: DashboardPage,
        clips: ClipsPage,
        golfers: GolfersPage,
        overlays: OverlaysPage,
        schedules: SchedulesPage,
        settings: SettingsPage
    },

    init() {
        // Always attach login/register form handlers
        this.setupAuth();

        // Check auth
        if (!Auth.isLoggedIn()) {
            this.showLogin();
            return;
        }

        this.hideLogin();
        this.setupNav();
        this.connectSocket();

        // Route to initial page
        const hash = window.location.hash.slice(1) || 'dashboard';
        this.navigate(hash);

        // Handle hash changes
        window.addEventListener('hashchange', () => {
            const page = window.location.hash.slice(1) || 'dashboard';
            this.navigate(page);
        });
    },

    setupNav() {
        document.querySelectorAll('.nav-link').forEach(link => {
            link.addEventListener('click', (e) => {
                e.preventDefault();
                const page = link.dataset.page;
                window.location.hash = page;
            });
        });
    },

    setupAuth() {
        // Update user info
        const userInfo = document.getElementById('user-info');
        if (Auth.user) {
            userInfo.textContent = Auth.user.username;
        }

        // Logout button
        document.getElementById('logout-btn').addEventListener('click', () => {
            Auth.logout();
        });

        // Login form
        document.getElementById('login-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const username = document.getElementById('login-username').value;
            const password = document.getElementById('login-password').value;
            const errorEl = document.getElementById('login-error');

            try {
                await Auth.login(username, password);
                window.location.reload();
            } catch (err) {
                errorEl.textContent = err.message;
            }
        });

        // Register form
        document.getElementById('register-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const username = document.getElementById('reg-username').value;
            const password = document.getElementById('reg-password').value;
            const errorEl = document.getElementById('reg-error');

            try {
                await Auth.register(username, password);
                errorEl.style.color = 'var(--accent-green)';
                errorEl.textContent = 'Account created! You can now login.';
                setTimeout(() => {
                    document.getElementById('register-form').style.display = 'none';
                    document.getElementById('login-form').style.display = 'block';
                }, 1500);
            } catch (err) {
                errorEl.textContent = err.message;
            }
        });

        // Toggle register/login
        document.getElementById('show-register').addEventListener('click', (e) => {
            e.preventDefault();
            document.getElementById('login-form').style.display = 'none';
            document.getElementById('register-form').style.display = 'block';
        });

        document.getElementById('show-login').addEventListener('click', (e) => {
            e.preventDefault();
            document.getElementById('register-form').style.display = 'none';
            document.getElementById('login-form').style.display = 'block';
        });
    },

    connectSocket() {
        this.socket = io();

        this.socket.on('connect', () => {
            console.log('Socket.IO connected');
        });

        this.socket.on('status', (status) => {
            if (this.currentPage && typeof this.currentPage.onStatus === 'function') {
                this.currentPage.onStatus(status);
            }
        });
    },

    navigate(pageName) {
        const PageModule = this.pages[pageName];
        if (!PageModule) {
            pageName = 'dashboard';
        }

        // Update nav
        document.querySelectorAll('.nav-link').forEach(link => {
            link.classList.toggle('active', link.dataset.page === pageName);
        });

        // Check permissions
        const adminPages = ['settings', 'overlays', 'schedules', 'golfers'];
        if (adminPages.includes(pageName) && !Auth.isAdmin()) {
            document.getElementById('page-container').innerHTML =
                '<div class="card"><h3>Access Denied</h3><p>Admin access required for this page.</p></div>';
            return;
        }

        // Cleanup current page
        if (this.currentPage && typeof this.currentPage.destroy === 'function') {
            this.currentPage.destroy();
        }

        // Render new page
        const container = document.getElementById('page-container');
        const page = this.pages[pageName];
        if (page) {
            this.currentPage = page;
            page.render(container);
        }
    },

    showLogin() {
        document.getElementById('login-overlay').classList.remove('hidden');
        document.getElementById('sidebar').style.display = 'none';
        document.getElementById('content').style.display = 'none';
    },

    hideLogin() {
        document.getElementById('login-overlay').classList.add('hidden');
        document.getElementById('sidebar').style.display = '';
        document.getElementById('content').style.display = '';
    }
};

// Initialize when DOM is ready
document.addEventListener('DOMContentLoaded', () => App.init());
