// Golfers page - CRUD golfer profiles

const GolfersPage = {
    golfers: [],

    async render(container) {
        container.innerHTML = `
            <div class="card-header">
                <h2>Golfers</h2>
                <button class="btn btn-primary" id="golfer-add">Add Golfer</button>
            </div>

            <div class="card">
                <table>
                    <thead>
                        <tr>
                            <th>Name</th>
                            <th>Display Name</th>
                            <th>Handicap</th>
                            <th>Actions</th>
                        </tr>
                    </thead>
                    <tbody id="golfers-table"></tbody>
                </table>
            </div>

            <div id="golfer-modal" class="modal hidden">
                <div class="modal-content">
                    <h3 id="golfer-modal-title">Add Golfer</h3>
                    <form id="golfer-form">
                        <label>First Name</label>
                        <input type="text" id="golfer-first" required>
                        <label>Last Name</label>
                        <input type="text" id="golfer-last" required>
                        <label>Display Name (optional)</label>
                        <input type="text" id="golfer-display">
                        <label>Handicap</label>
                        <input type="number" id="golfer-handicap" step="0.1">
                        <input type="hidden" id="golfer-edit-id">
                        <div style="display:flex;gap:8px;justify-content:flex-end">
                            <button type="button" class="btn" id="golfer-cancel">Cancel</button>
                            <button type="submit" class="btn btn-primary">Save</button>
                        </div>
                    </form>
                </div>
            </div>
        `;

        document.getElementById('golfer-add').addEventListener('click', () => this.openForm());
        document.getElementById('golfer-cancel').addEventListener('click', () => this.closeForm());
        document.getElementById('golfer-form').addEventListener('submit', (e) => this.saveGolfer(e));

        await this.loadGolfers();
    },

    async loadGolfers() {
        try {
            const res = await api('/golfers');
            if (!res.ok) throw new Error('Failed to load golfers');

            this.golfers = await res.json();
            this.renderTable();
        } catch (err) {
            document.getElementById('golfers-table').innerHTML =
                `<tr><td colspan="4" style="color:var(--text-secondary)">${err.message}</td></tr>`;
        }
    },

    renderTable() {
        const tbody = document.getElementById('golfers-table');

        if (this.golfers.length === 0) {
            tbody.innerHTML = '<tr><td colspan="4" style="color:var(--text-secondary)">No golfers yet.</td></tr>';
            return;
        }

        tbody.innerHTML = this.golfers.map(g => `
            <tr>
                <td>${g.firstName} ${g.lastName}</td>
                <td>${g.displayName || '--'}</td>
                <td>${g.handicap != null ? g.handicap : '--'}</td>
                <td>
                    <button class="btn btn-sm" onclick="GolfersPage.editGolfer('${g.id}')">Edit</button>
                    <button class="btn btn-sm btn-danger" onclick="GolfersPage.deleteGolfer('${g.id}')">Delete</button>
                </td>
            </tr>
        `).join('');
    },

    openForm(golfer = null) {
        document.getElementById('golfer-modal-title').textContent = golfer ? 'Edit Golfer' : 'Add Golfer';
        document.getElementById('golfer-first').value = golfer?.firstName || '';
        document.getElementById('golfer-last').value = golfer?.lastName || '';
        document.getElementById('golfer-display').value = golfer?.displayName || '';
        document.getElementById('golfer-handicap').value = golfer?.handicap || '';
        document.getElementById('golfer-edit-id').value = golfer?.id || '';
        document.getElementById('golfer-modal').classList.remove('hidden');
    },

    closeForm() {
        document.getElementById('golfer-modal').classList.add('hidden');
    },

    editGolfer(id) {
        const golfer = this.golfers.find(g => g.id === id);
        if (golfer) this.openForm(golfer);
    },

    async saveGolfer(e) {
        e.preventDefault();

        const id = document.getElementById('golfer-edit-id').value;
        const data = {
            firstName: document.getElementById('golfer-first').value,
            lastName: document.getElementById('golfer-last').value,
            displayName: document.getElementById('golfer-display').value || null,
            handicap: document.getElementById('golfer-handicap').value ? parseFloat(document.getElementById('golfer-handicap').value) : null,
            isActive: true
        };

        try {
            if (id) {
                await api(`/golfers/${id}`, { method: 'PUT', body: JSON.stringify(data) });
            } else {
                await api('/golfers', { method: 'POST', body: JSON.stringify(data) });
            }

            this.closeForm();
            await this.loadGolfers();
        } catch (err) {
            alert('Error saving golfer: ' + err.message);
        }
    },

    async deleteGolfer(id) {
        if (!confirm('Delete this golfer?')) return;

        try {
            await api(`/golfers/${id}`, { method: 'DELETE' });
            await this.loadGolfers();
        } catch (err) {
            alert('Error deleting golfer: ' + err.message);
        }
    },

    destroy() {}
};
