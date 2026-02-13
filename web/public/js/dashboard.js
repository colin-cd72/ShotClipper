// Dashboard page - live monitoring

const DashboardPage = {
    streamSocket: null,
    durationInterval: null,

    render(container) {
        container.innerHTML = `
            <h2 style="margin-bottom: 16px">Dashboard</h2>

            <div class="grid grid-4" style="margin-bottom: 16px">
                <div class="stat-box">
                    <div class="label">Status</div>
                    <div class="value" id="dash-status" style="font-size:16px">
                        <span class="status-dot offline"></span>Offline
                    </div>
                </div>
                <div class="stat-box">
                    <div class="label">Swings</div>
                    <div class="value" id="dash-swings">0</div>
                </div>
                <div class="stat-box">
                    <div class="label">Practice</div>
                    <div class="value" id="dash-practice">0</div>
                </div>
                <div class="stat-box">
                    <div class="label">Recording</div>
                    <div class="value" id="dash-recording" style="font-size:16px">
                        <span class="badge badge-red">Off</span>
                    </div>
                </div>
            </div>

            <div class="grid grid-2">
                <div>
                    <div class="card">
                        <div class="card-header">
                            <h3>Live Stream</h3>
                            <span id="dash-viewers" class="badge badge-blue">0 viewers</span>
                        </div>
                        <div class="stream-container">
                            <img id="stream-frame" alt="Live Stream" style="display:none">
                            <div id="stream-placeholder" style="display:flex;align-items:center;justify-content:center;height:100%;color:var(--text-secondary)">
                                No stream available
                            </div>
                        </div>
                    </div>
                </div>

                <div>
                    <div class="card">
                        <div class="card-header">
                            <h3>Session</h3>
                            <span id="dash-golfer" class="badge badge-blue">No golfer</span>
                        </div>
                        <div>
                            <div style="margin-bottom:12px">
                                <div class="label">Auto-Cut State</div>
                                <div id="dash-autocut" style="font-size:14px;font-weight:500">Disabled</div>
                            </div>
                            <div style="margin-bottom:12px">
                                <div class="label">Session Duration</div>
                                <div id="dash-duration" class="timer" style="font-size:20px">00:00:00</div>
                            </div>
                            <div>
                                <div class="label">Uptime</div>
                                <div id="dash-uptime" class="timer">--</div>
                            </div>
                        </div>
                    </div>

                    <div class="card">
                        <div class="card-header"><h3>Cut History</h3></div>
                        <div id="dash-history" style="max-height:200px;overflow-y:auto">
                            <p style="color:var(--text-secondary);font-size:13px">No cuts yet</p>
                        </div>
                    </div>
                </div>
            </div>
        `;

        this.connectStream();
    },

    connectStream() {
        this.streamSocket = io('/stream');

        this.streamSocket.on('frame', (data) => {
            const blob = new Blob([data], { type: 'image/jpeg' });
            const url = URL.createObjectURL(blob);
            const img = document.getElementById('stream-frame');
            const placeholder = document.getElementById('stream-placeholder');

            if (img) {
                if (img._prevUrl) URL.revokeObjectURL(img._prevUrl);
                img.src = url;
                img._prevUrl = url;
                img.style.display = 'block';
            }
            if (placeholder) placeholder.style.display = 'none';
        });
    },

    onStatus(status) {
        // Status indicator
        const statusEl = document.getElementById('dash-status');
        if (statusEl) {
            const desktopOnline = status._desktopOnline !== false;
            const isStreaming = status.streamingState === 'Running';
            let dotClass, label;
            if (!desktopOnline) { dotClass = 'offline'; label = 'Desktop Offline'; }
            else if (isStreaming) { dotClass = 'live'; label = 'Online'; }
            else { dotClass = 'waiting'; label = 'Connected'; }
            statusEl.innerHTML = `<span class="status-dot ${dotClass}"></span>${label}`;
        }

        // Stats
        const swingsEl = document.getElementById('dash-swings');
        if (swingsEl) swingsEl.textContent = status.swingCount || 0;

        const practiceEl = document.getElementById('dash-practice');
        if (practiceEl) practiceEl.textContent = status.practiceSwingCount || 0;

        // Recording
        const recEl = document.getElementById('dash-recording');
        if (recEl) {
            if (status.isRecording) {
                recEl.innerHTML = '<span class="badge badge-green">Recording</span>';
            } else {
                recEl.innerHTML = '<span class="badge badge-red">Off</span>';
            }
        }

        // Viewers
        const viewersEl = document.getElementById('dash-viewers');
        if (viewersEl) viewersEl.textContent = `${status.connectedViewers || 0} viewers`;

        // Golfer
        const golferEl = document.getElementById('dash-golfer');
        if (golferEl) golferEl.textContent = status.golferName || 'No golfer';

        // Auto-cut state
        const autocutEl = document.getElementById('dash-autocut');
        if (autocutEl) autocutEl.textContent = status.autoCutState || 'Disabled';

        // Uptime
        const uptimeEl = document.getElementById('dash-uptime');
        if (uptimeEl && status.uptime) {
            const s = Math.floor(status.uptime);
            const h = Math.floor(s / 3600);
            const m = Math.floor((s % 3600) / 60);
            uptimeEl.textContent = `${h}h ${m}m ${s % 60}s`;
        }
    },

    destroy() {
        if (this.streamSocket) {
            this.streamSocket.disconnect();
            this.streamSocket = null;
        }
        if (this.durationInterval) {
            clearInterval(this.durationInterval);
            this.durationInterval = null;
        }
    }
};
