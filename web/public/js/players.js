// Players page - quick player selection for lower third

const PlayersPage = {
    golfers: [],
    activePlayerId: null,

    async render(container) {
        container.innerHTML = `
            <div class="card-header">
                <h2>Players</h2>
                <button class="btn btn-danger" id="player-clear">Clear Lower Third</button>
            </div>
            <div id="player-grid" class="player-grid"></div>
        `;

        document.getElementById('player-clear').addEventListener('click', () => this.clearLowerThird());

        await this.loadPlayers();
    },

    async loadPlayers() {
        try {
            const res = await api('/golfers');
            if (!res.ok) throw new Error('Failed to load golfers');

            this.golfers = await res.json();
            this.renderGrid();
        } catch (err) {
            document.getElementById('player-grid').innerHTML =
                `<p style="color:var(--text-secondary)">${err.message}</p>`;
        }
    },

    renderGrid() {
        const grid = document.getElementById('player-grid');

        if (this.golfers.length === 0) {
            grid.innerHTML = '<p style="color:var(--text-secondary)">No players yet. Add golfers on the Golfers page.</p>';
            return;
        }

        grid.innerHTML = this.golfers.map(g => {
            const displayName = g.displayName || `${g.firstName} ${g.lastName}`.trim();
            const isActive = this.activePlayerId === g.id;
            return `<button class="player-btn${isActive ? ' player-btn-active' : ''}"
                            data-id="${g.id}" data-name="${displayName}"
                            onclick="PlayersPage.selectPlayer('${g.id}', '${displayName.replace(/'/g, "\\'")}')">${displayName}</button>`;
        }).join('');
    },

    async selectPlayer(id, name) {
        try {
            const res = await api('/lowerthird', {
                method: 'POST',
                body: JSON.stringify({ text: name })
            });
            if (!res.ok) throw new Error('Failed to set lower third');

            this.activePlayerId = id;
            this.renderGrid();
        } catch (err) {
            alert('Error: ' + err.message);
        }
    },

    async clearLowerThird() {
        try {
            const res = await api('/lowerthird', { method: 'DELETE' });
            if (!res.ok) throw new Error('Failed to clear lower third');

            this.activePlayerId = null;
            this.renderGrid();
        } catch (err) {
            alert('Error: ' + err.message);
        }
    },

    destroy() {
        this.activePlayerId = null;
    }
};
