// Dashboard page - live monitoring

const DashboardPage = {
    streamSocket: null,
    durationInterval: null,
    _isStreaming: false,
    _isRecording: false,

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
                            <div style="display:flex;gap:8px;align-items:center">
                                <span id="dash-viewers" class="badge badge-blue">0 viewers</span>
                                <button class="btn btn-sm" id="dash-stream-toggle">Start Stream</button>
                            </div>
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
                        <div class="card-header">
                            <h3>Recording</h3>
                            <button class="btn btn-sm" id="dash-rec-toggle">Start Recording</button>
                        </div>
                        <div id="dash-rec-name-row" style="display:none;margin-bottom:8px">
                            <label>Recording Name (optional)</label>
                            <input type="text" id="dash-rec-name" placeholder="e.g. Session 1">
                        </div>
                        <div id="dash-rec-status" style="font-size:13px;color:var(--text-secondary)">Not recording</div>
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

        document.getElementById('dash-stream-toggle').addEventListener('click', () => this.toggleStream());
        document.getElementById('dash-rec-toggle').addEventListener('click', () => this.toggleRecording());

        this.connectStream();
    },

    async toggleStream() {
        const btn = document.getElementById('dash-stream-toggle');
        const newState = !this._isStreaming;
        btn.disabled = true;
        btn.textContent = newState ? 'Starting...' : 'Stopping...';

        try {
            const res = await api('/stream', {
                method: 'POST',
                body: JSON.stringify({ enabled: newState })
            });
            if (!res.ok) throw new Error('Failed');
        } catch (err) {
            alert('Error toggling stream: ' + err.message);
        } finally {
            btn.disabled = false;
            this.updateStreamButton();
        }
    },

    async toggleRecording() {
        const btn = document.getElementById('dash-rec-toggle');
        const nameRow = document.getElementById('dash-rec-name-row');
        const nameInput = document.getElementById('dash-rec-name');

        if (!this._isRecording) {
            // Show name input if hidden, then start on second click
            if (nameRow.style.display === 'none') {
                nameRow.style.display = 'block';
                nameInput.focus();
                return;
            }
        }

        const newState = !this._isRecording;
        btn.disabled = true;
        btn.textContent = newState ? 'Starting...' : 'Stopping...';

        try {
            const body = { enabled: newState };
            if (newState && nameInput.value.trim()) {
                body.name = nameInput.value.trim();
            }

            const res = await api('/recording', {
                method: 'POST',
                body: JSON.stringify(body)
            });
            if (!res.ok) throw new Error('Failed');

            nameRow.style.display = 'none';
            nameInput.value = '';
        } catch (err) {
            alert('Error toggling recording: ' + err.message);
        } finally {
            btn.disabled = false;
            this.updateRecordingButton();
        }
    },

    updateStreamButton() {
        const btn = document.getElementById('dash-stream-toggle');
        if (!btn) return;
        if (this._isStreaming) {
            btn.textContent = 'Stop Stream';
            btn.className = 'btn btn-sm btn-danger';
        } else {
            btn.textContent = 'Start Stream';
            btn.className = 'btn btn-sm btn-primary';
        }
    },

    updateRecordingButton() {
        const btn = document.getElementById('dash-rec-toggle');
        const nameRow = document.getElementById('dash-rec-name-row');
        const statusEl = document.getElementById('dash-rec-status');
        if (!btn) return;
        if (this._isRecording) {
            btn.textContent = 'Stop Recording';
            btn.className = 'btn btn-sm btn-danger';
            if (nameRow) nameRow.style.display = 'none';
            if (statusEl) statusEl.style.color = 'var(--accent-green)';
        } else {
            btn.textContent = 'Start Recording';
            btn.className = 'btn btn-sm btn-primary';
            if (statusEl) {
                statusEl.textContent = 'Not recording';
                statusEl.style.color = 'var(--text-secondary)';
            }
        }
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

            // Track streaming state for toggle button
            const wasStreaming = this._isStreaming;
            this._isStreaming = isStreaming;
            if (wasStreaming !== isStreaming) this.updateStreamButton();
        }

        // Stats
        const swingsEl = document.getElementById('dash-swings');
        if (swingsEl) swingsEl.textContent = status.swingCount || 0;

        const practiceEl = document.getElementById('dash-practice');
        if (practiceEl) practiceEl.textContent = status.practiceSwingCount || 0;

        // Recording status
        const recEl = document.getElementById('dash-recording');
        if (recEl) {
            if (status.isRecording) {
                recEl.innerHTML = '<span class="badge badge-green">Recording</span>';
            } else {
                recEl.innerHTML = '<span class="badge badge-red">Off</span>';
            }
        }

        // Track recording state for toggle button
        const wasRecording = this._isRecording;
        this._isRecording = !!status.isRecording;
        if (wasRecording !== this._isRecording) this.updateRecordingButton();

        const recStatusEl = document.getElementById('dash-rec-status');
        if (recStatusEl && status.isRecording) {
            recStatusEl.textContent = status.recordingStatusText || 'Recording...';
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
