// Clips page - browse, preview, download exported swing clips

const ClipsPage = {
    clips: [],

    async render(container) {
        container.innerHTML = `
            <div class="card-header">
                <h2>Clips</h2>
                <button class="btn" id="clips-refresh">Refresh</button>
            </div>

            <div id="clips-loading" style="color:var(--text-secondary)">Loading clips...</div>
            <div id="clips-grid" class="clip-grid"></div>

            <div id="clip-modal" class="modal hidden">
                <div class="modal-content" style="min-width:640px">
                    <div class="card-header">
                        <h3 id="clip-modal-title">Clip</h3>
                        <button class="btn btn-sm" id="clip-modal-close">Close</button>
                    </div>
                    <video id="clip-player" class="video-player" controls></video>
                    <div style="margin-top:12px;display:flex;gap:8px">
                        <a id="clip-download" class="btn btn-primary" download>Download</a>
                    </div>
                </div>
            </div>
        `;

        document.getElementById('clips-refresh').addEventListener('click', () => this.loadClips());
        document.getElementById('clip-modal-close').addEventListener('click', () => this.closeModal());

        await this.loadClips();
    },

    async loadClips() {
        const grid = document.getElementById('clips-grid');
        const loading = document.getElementById('clips-loading');

        try {
            const res = await api('/clips');
            if (!res.ok) throw new Error('Failed to load clips');

            this.clips = await res.json();
            loading.style.display = 'none';

            if (this.clips.length === 0) {
                grid.innerHTML = '<p style="color:var(--text-secondary)">No clips exported yet.</p>';
                return;
            }

            grid.innerHTML = this.clips.map((clip, i) => `
                <div class="clip-card" data-index="${i}">
                    <div class="preview">
                        <span style="font-size:32px">&#9654;</span>
                    </div>
                    <div class="info">
                        <div class="name" title="${clip.name}">${clip.name}</div>
                        <div class="meta">
                            ${this.formatSize(clip.size)} &middot;
                            ${new Date(clip.created).toLocaleDateString()}
                        </div>
                    </div>
                </div>
            `).join('');

            // Click handlers
            grid.querySelectorAll('.clip-card').forEach(card => {
                card.addEventListener('click', () => {
                    const idx = parseInt(card.dataset.index);
                    this.openClip(this.clips[idx]);
                });
            });
        } catch (err) {
            loading.textContent = `Error: ${err.message}`;
        }
    },

    openClip(clip) {
        const modal = document.getElementById('clip-modal');
        const player = document.getElementById('clip-player');
        const title = document.getElementById('clip-modal-title');
        const downloadLink = document.getElementById('clip-download');

        title.textContent = clip.name;
        const downloadUrl = `/api/clips/${encodeURIComponent(clip.name)}/download`;
        player.src = downloadUrl;
        downloadLink.href = downloadUrl;
        downloadLink.download = clip.name;

        modal.classList.remove('hidden');
    },

    closeModal() {
        const modal = document.getElementById('clip-modal');
        const player = document.getElementById('clip-player');
        player.pause();
        player.src = '';
        modal.classList.add('hidden');
    },

    formatSize(bytes) {
        if (bytes > 1e9) return (bytes / 1e9).toFixed(1) + ' GB';
        if (bytes > 1e6) return (bytes / 1e6).toFixed(1) + ' MB';
        if (bytes > 1e3) return (bytes / 1e3).toFixed(1) + ' KB';
        return bytes + ' B';
    },

    destroy() {}
};
