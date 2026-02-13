// Golfers page - CRUD golfer profiles

const GolfersPage = {
    golfers: [],

    async render(container) {
        container.innerHTML = `
            <div class="card-header">
                <h2>Golfers</h2>
                <div style="display:flex;gap:8px">
                    <button class="btn" id="golfer-import">Import CSV</button>
                    <button class="btn btn-primary" id="golfer-add">Add Golfer</button>
                </div>
            </div>

            <div class="card">
                <table>
                    <thead>
                        <tr>
                            <th>Name</th>
                            <th>Display Name</th>
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
                        <input type="hidden" id="golfer-edit-id">
                        <div style="display:flex;gap:8px;justify-content:flex-end">
                            <button type="button" class="btn" id="golfer-cancel">Cancel</button>
                            <button type="submit" class="btn btn-primary">Save</button>
                        </div>
                    </form>
                </div>
            </div>

            <div id="import-modal" class="modal hidden">
                <div class="modal-content">
                    <h3>Import Players from CSV</h3>
                    <label>Select CSV file</label>
                    <input type="file" id="import-file" accept=".csv,.txt">
                    <div id="import-preview" style="margin:12px 0;max-height:300px;overflow-y:auto"></div>
                    <div style="display:flex;gap:8px;justify-content:flex-end">
                        <button type="button" class="btn" id="import-cancel">Cancel</button>
                        <button type="button" class="btn btn-primary" id="import-confirm" disabled>Import</button>
                    </div>
                </div>
            </div>
        `;

        document.getElementById('golfer-add').addEventListener('click', () => this.openForm());
        document.getElementById('golfer-cancel').addEventListener('click', () => this.closeForm());
        document.getElementById('golfer-form').addEventListener('submit', (e) => this.saveGolfer(e));
        document.getElementById('golfer-import').addEventListener('click', () => this.openImport());
        document.getElementById('import-cancel').addEventListener('click', () => this.closeImport());
        document.getElementById('import-file').addEventListener('change', (e) => this.parseCSV(e));
        document.getElementById('import-confirm').addEventListener('click', () => this.confirmImport());

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
                `<tr><td colspan="3" style="color:var(--text-secondary)">${err.message}</td></tr>`;
        }
    },

    renderTable() {
        const tbody = document.getElementById('golfers-table');

        if (this.golfers.length === 0) {
            tbody.innerHTML = '<tr><td colspan="3" style="color:var(--text-secondary)">No golfers yet.</td></tr>';
            return;
        }

        tbody.innerHTML = this.golfers.map(g => `
            <tr>
                <td>${g.firstName} ${g.lastName}</td>
                <td>${g.displayName || '--'}</td>
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

    // CSV Import
    _parsedNames: [],

    openImport() {
        this._parsedNames = [];
        document.getElementById('import-file').value = '';
        document.getElementById('import-preview').innerHTML = '<p style="color:var(--text-secondary)">Select a CSV file to preview names.</p>';
        document.getElementById('import-confirm').disabled = true;
        document.getElementById('import-modal').classList.remove('hidden');
    },

    closeImport() {
        document.getElementById('import-modal').classList.add('hidden');
    },

    parseCSV(e) {
        const file = e.target.files[0];
        if (!file) return;

        const reader = new FileReader();
        reader.onload = (evt) => {
            const text = evt.target.result;
            const lines = text.split(/\r?\n/).filter(l => l.trim());
            if (lines.length === 0) return;

            // Parse header
            const sep = lines[0].includes('\t') ? '\t' : ',';
            const headers = lines[0].split(sep).map(h => h.trim().replace(/^["']|["']$/g, '').toLowerCase());

            const firstNameIdx = headers.findIndex(h => h === 'first name' || h === 'firstname' || h === 'first');
            const lastNameIdx = headers.findIndex(h => h === 'last name' || h === 'lastname' || h === 'last');
            const nameIdx = headers.findIndex(h => h === 'name' || h === 'player' || h === 'player name' || h === 'playername');

            const names = [];
            const startRow = (firstNameIdx >= 0 || lastNameIdx >= 0 || nameIdx >= 0) ? 1 : 0;

            for (let i = startRow; i < lines.length; i++) {
                const cols = lines[i].split(sep).map(c => c.trim().replace(/^["']|["']$/g, ''));
                let fullName = '';

                if (firstNameIdx >= 0 && lastNameIdx >= 0) {
                    fullName = ((cols[firstNameIdx] || '') + ' ' + (cols[lastNameIdx] || '')).trim();
                } else if (nameIdx >= 0) {
                    fullName = cols[nameIdx] || '';
                } else {
                    fullName = cols[0] || '';
                }

                if (fullName) names.push(fullName);
            }

            this._parsedNames = names;

            const preview = document.getElementById('import-preview');
            if (names.length === 0) {
                preview.innerHTML = '<p style="color:var(--accent-red)">No names found in file.</p>';
                document.getElementById('import-confirm').disabled = true;
            } else {
                preview.innerHTML = `<p style="margin-bottom:8px;color:var(--text-secondary)">${names.length} player(s) found:</p>` +
                    names.map(n => `<div style="padding:4px 0;border-bottom:1px solid var(--border)">${n}</div>`).join('');
                document.getElementById('import-confirm').disabled = false;
            }
        };
        reader.readAsText(file);
    },

    async confirmImport() {
        if (this._parsedNames.length === 0) return;

        try {
            document.getElementById('import-confirm').disabled = true;
            document.getElementById('import-confirm').textContent = 'Importing...';

            const res = await api('/golfers/import', {
                method: 'POST',
                body: JSON.stringify({ names: this._parsedNames })
            });

            if (!res.ok) throw new Error('Import failed');
            const result = await res.json();

            this.closeImport();
            await this.loadGolfers();
            alert(`Successfully imported ${result.imported} player(s).`);
        } catch (err) {
            alert('Error importing: ' + err.message);
        } finally {
            document.getElementById('import-confirm').textContent = 'Import';
            document.getElementById('import-confirm').disabled = false;
        }
    },

    destroy() {}
};
