// Settings page - desktop connection, streaming quality, auto-upload, sensitivity, API key

const SettingsPage = {
    render(container) {
        container.innerHTML = `
            <h2 style="margin-bottom:16px">Settings</h2>

            <div class="grid grid-2">
                <div class="card">
                    <div class="card-header">
                        <h3>Desktop Connection</h3>
                        <span id="conn-status" class="badge badge-red">Unknown</span>
                    </div>
                    <form id="connection-form">
                        <label>Desktop App URL</label>
                        <input type="text" id="set-desktop-url" placeholder="http://192.168.1.100:8080">
                        <label>API Key</label>
                        <input type="text" id="set-desktop-key" placeholder="Shared API key">
                        <div style="display:flex;gap:8px;align-items:center;margin-bottom:12px">
                            <button type="submit" class="btn btn-primary">Save</button>
                            <button type="button" class="btn" id="test-connection">Test Connection</button>
                        </div>
                        <div style="font-size:12px;color:var(--text-secondary)">
                            <div>Last seen: <span id="conn-last-seen">--</span></div>
                            <div>Hostname: <span id="conn-hostname">--</span></div>
                        </div>
                        <p id="conn-test-result" style="font-size:13px;margin-top:8px"></p>
                    </form>
                    <div style="margin-top:16px;padding-top:12px;border-top:1px solid var(--border)">
                        <p style="font-size:12px;color:var(--text-secondary)">
                            The desktop app can auto-register its IP by calling <code>POST /register</code> with
                            <code>{"{ apiKey, ip, port }"}</code>. This handles DHCP changes automatically.
                        </p>
                    </div>
                </div>

                <div class="card">
                    <div class="card-header"><h3>Streaming Quality</h3></div>
                    <form id="streaming-form">
                        <label>Resolution</label>
                        <select id="set-resolution">
                            <option value="480p">480p</option>
                            <option value="720p" selected>720p</option>
                            <option value="1080p">1080p</option>
                        </select>
                        <label>Bitrate (kbps)</label>
                        <input type="number" id="set-bitrate" value="4000" min="500" max="20000" step="500">
                        <label>FPS</label>
                        <select id="set-fps">
                            <option value="15">15</option>
                            <option value="30" selected>30</option>
                            <option value="60">60</option>
                        </select>
                        <button type="submit" class="btn btn-primary">Save Streaming Settings</button>
                    </form>
                </div>

                <div class="card">
                    <div class="card-header"><h3>Detection Sensitivity</h3></div>
                    <form id="sensitivity-form">
                        <label>Preset</label>
                        <select id="set-sensitivity">
                            <option value="High">High Sensitivity</option>
                            <option value="Default" selected>Default</option>
                            <option value="Low">Low Sensitivity</option>
                        </select>
                        <p style="font-size:12px;color:var(--text-secondary);margin-bottom:12px">
                            High = more sensitive detection, Low = fewer false positives
                        </p>
                        <button type="submit" class="btn btn-primary">Apply Preset</button>
                    </form>
                </div>

                <div class="card">
                    <div class="card-header"><h3>Auto-Upload</h3></div>
                    <form id="upload-form">
                        <label>Auto-Upload Clips</label>
                        <select id="set-auto-upload">
                            <option value="false">Disabled</option>
                            <option value="true">Enabled</option>
                        </select>
                        <label>Upload Provider</label>
                        <input type="text" id="set-upload-provider" placeholder="e.g., s3, azure, gcs">
                        <label>Remote Path</label>
                        <input type="text" id="set-upload-path" placeholder="e.g., golf/clips/">
                        <button type="submit" class="btn btn-primary">Save Upload Settings</button>
                    </form>
                </div>
            </div>
        `;

        this.setupForms();
        this.loadConnectionInfo();
        this.loadDesktopSettings();
    },

    setupForms() {
        // Desktop connection
        document.getElementById('connection-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            try {
                const url = document.getElementById('set-desktop-url').value.replace(/\/+$/, '');
                const key = document.getElementById('set-desktop-key').value;

                await fetch('/panel/config', {
                    method: 'PUT',
                    headers: Auth.getHeaders(),
                    body: JSON.stringify({
                        desktop_api_url: url,
                        desktop_api_key: key
                    })
                });
                alert('Desktop connection saved. Stream will reconnect.');
                this.loadConnectionInfo();
            } catch (err) { alert('Error: ' + err.message); }
        });

        document.getElementById('test-connection').addEventListener('click', async () => {
            const resultEl = document.getElementById('conn-test-result');
            resultEl.textContent = 'Testing...';
            resultEl.style.color = 'var(--text-secondary)';

            try {
                const res = await api('/status');
                if (res.ok) {
                    const data = await res.json();
                    resultEl.textContent = `Connected! State: ${data.streamingState || 'OK'}`;
                    resultEl.style.color = 'var(--accent-green)';
                } else {
                    resultEl.textContent = `Error: HTTP ${res.status}`;
                    resultEl.style.color = 'var(--accent-red)';
                }
            } catch (err) {
                resultEl.textContent = `Failed: ${err.message}`;
                resultEl.style.color = 'var(--accent-red)';
            }
        });

        // Streaming settings (proxied to desktop)
        document.getElementById('streaming-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            try {
                await this.saveDesktopSetting('streaming.resolution', document.getElementById('set-resolution').value);
                await this.saveDesktopSetting('streaming.bitrate', parseInt(document.getElementById('set-bitrate').value));
                alert('Streaming settings saved');
            } catch (err) { alert('Error: ' + err.message); }
        });

        // Sensitivity
        document.getElementById('sensitivity-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            try {
                await this.saveDesktopSetting('golf.sensitivityPreset', document.getElementById('set-sensitivity').value);
                alert('Sensitivity preset applied');
            } catch (err) { alert('Error: ' + err.message); }
        });

        // Upload settings
        document.getElementById('upload-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            try {
                await this.saveDesktopSetting('golf.autoUpload', document.getElementById('set-auto-upload').value === 'true');
                await this.saveDesktopSetting('golf.autoUploadProvider', document.getElementById('set-upload-provider').value);
                await this.saveDesktopSetting('golf.autoUploadRemotePath', document.getElementById('set-upload-path').value);
                alert('Upload settings saved');
            } catch (err) { alert('Error: ' + err.message); }
        });
    },

    async loadConnectionInfo() {
        try {
            const res = await fetch('/panel/connection', { headers: Auth.getHeaders() });
            if (res.ok) {
                const data = await res.json();
                document.getElementById('set-desktop-url').value = data.url !== 'not configured' ? data.url : '';
                document.getElementById('conn-last-seen').textContent = data.lastSeen !== 'never'
                    ? new Date(data.lastSeen).toLocaleString() : 'Never';
                document.getElementById('conn-hostname').textContent = data.hostname;

                // Load stored API key
                const configRes = await fetch('/panel/config', { headers: Auth.getHeaders() });
                if (configRes.ok) {
                    const config = await configRes.json();
                    if (config.desktop_api_key) {
                        document.getElementById('set-desktop-key').value = config.desktop_api_key;
                    }
                }

                // Check connection status
                const statusBadge = document.getElementById('conn-status');
                if (data.lastSeen !== 'never') {
                    const ago = Date.now() - new Date(data.lastSeen).getTime();
                    if (ago < 30000) {
                        statusBadge.className = 'badge badge-green';
                        statusBadge.textContent = 'Online';
                    } else if (ago < 120000) {
                        statusBadge.className = 'badge badge-orange';
                        statusBadge.textContent = 'Stale';
                    } else {
                        statusBadge.className = 'badge badge-red';
                        statusBadge.textContent = 'Offline';
                    }
                }
            }
        } catch {}
    },

    async loadDesktopSettings() {
        const keys = [
            ['streaming.resolution', 'set-resolution'],
            ['streaming.bitrate', 'set-bitrate'],
            ['golf.autoUpload', 'set-auto-upload'],
            ['golf.autoUploadProvider', 'set-upload-provider'],
            ['golf.autoUploadRemotePath', 'set-upload-path']
        ];

        for (const [key, elementId] of keys) {
            try {
                const res = await api(`/settings/${key}`);
                if (res.ok) {
                    const data = await res.json();
                    if (data.value != null) {
                        const el = document.getElementById(elementId);
                        if (el) el.value = data.value;
                    }
                }
            } catch {}
        }
    },

    async saveDesktopSetting(key, value) {
        const res = await api(`/settings/${key}`, {
            method: 'PUT',
            body: JSON.stringify({ value })
        });
        if (!res.ok) throw new Error('Failed to save setting');
    },

    destroy() {}
};
