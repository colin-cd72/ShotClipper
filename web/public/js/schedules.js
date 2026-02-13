// Schedules page - recording schedule management

const SchedulesPage = {
    schedules: [],

    async render(container) {
        container.innerHTML = `
            <div class="card-header">
                <h2>Schedules</h2>
                <button class="btn btn-primary" id="schedule-add">New Schedule</button>
            </div>

            <div class="card">
                <table>
                    <thead>
                        <tr>
                            <th>Name</th>
                            <th>Start Time</th>
                            <th>Duration</th>
                            <th>Recurrence</th>
                            <th>Status</th>
                            <th>Actions</th>
                        </tr>
                    </thead>
                    <tbody id="schedules-table"></tbody>
                </table>
            </div>

            <div id="schedule-modal" class="modal hidden">
                <div class="modal-content">
                    <h3>New Schedule</h3>
                    <form id="schedule-form">
                        <label>Name</label>
                        <input type="text" id="sched-name" required>
                        <label>Start Date & Time</label>
                        <input type="datetime-local" id="sched-start" required>
                        <div class="grid grid-2">
                            <div>
                                <label>Duration (hours)</label>
                                <input type="number" id="sched-hours" value="1" min="0" max="24">
                            </div>
                            <div>
                                <label>Duration (minutes)</label>
                                <input type="number" id="sched-minutes" value="0" min="0" max="59">
                            </div>
                        </div>
                        <label>Recurrence</label>
                        <select id="sched-recurrence">
                            <option value="none">None (one-time)</option>
                            <option value="daily">Daily</option>
                            <option value="weekly">Weekly</option>
                        </select>
                        <div style="display:flex;gap:8px;justify-content:flex-end;margin-top:12px">
                            <button type="button" class="btn" id="sched-cancel">Cancel</button>
                            <button type="submit" class="btn btn-primary">Create</button>
                        </div>
                    </form>
                </div>
            </div>
        `;

        document.getElementById('schedule-add').addEventListener('click', () => this.openForm());
        document.getElementById('sched-cancel').addEventListener('click', () => this.closeForm());
        document.getElementById('schedule-form').addEventListener('submit', (e) => this.createSchedule(e));

        await this.loadSchedules();
    },

    async loadSchedules() {
        try {
            const res = await api('/schedules');
            if (!res.ok) throw new Error('Failed to load schedules');

            this.schedules = await res.json();
            this.renderTable();
        } catch (err) {
            document.getElementById('schedules-table').innerHTML =
                `<tr><td colspan="6" style="color:var(--text-secondary)">${err.message}</td></tr>`;
        }
    },

    renderTable() {
        const tbody = document.getElementById('schedules-table');

        if (this.schedules.length === 0) {
            tbody.innerHTML = '<tr><td colspan="6" style="color:var(--text-secondary)">No schedules.</td></tr>';
            return;
        }

        tbody.innerHTML = this.schedules.map(s => {
            const start = new Date(s.startTime);
            const durH = Math.floor(s.duration / 3600);
            const durM = Math.floor((s.duration % 3600) / 60);
            const recurrence = s.recurrence?.type || 'None';
            const isUpcoming = start > new Date();

            return `
                <tr>
                    <td>${s.name}</td>
                    <td>${start.toLocaleString()}</td>
                    <td>${durH}h ${durM}m</td>
                    <td>${recurrence}</td>
                    <td>
                        <span class="badge ${isUpcoming ? 'badge-green' : 'badge-orange'}">
                            ${isUpcoming ? 'Upcoming' : 'Past'}
                        </span>
                    </td>
                    <td>
                        <button class="btn btn-sm btn-danger" onclick="SchedulesPage.deleteSchedule('${s.id}')">Delete</button>
                    </td>
                </tr>
            `;
        }).join('');
    },

    openForm() {
        // Set default start time to next hour
        const now = new Date();
        now.setHours(now.getHours() + 1, 0, 0, 0);
        document.getElementById('sched-start').value = now.toISOString().slice(0, 16);
        document.getElementById('schedule-modal').classList.remove('hidden');
    },

    closeForm() {
        document.getElementById('schedule-modal').classList.add('hidden');
    },

    async createSchedule(e) {
        e.preventDefault();

        const hours = parseInt(document.getElementById('sched-hours').value) || 0;
        const minutes = parseInt(document.getElementById('sched-minutes').value) || 0;
        const recurrenceType = document.getElementById('sched-recurrence').value;

        const data = {
            name: document.getElementById('sched-name').value,
            startTime: new Date(document.getElementById('sched-start').value).toISOString(),
            duration: `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:00`,
            preset: { name: 'Medium' },
            filenameTemplate: '{name}_{datetime}',
            recurrence: recurrenceType !== 'none' ? { type: recurrenceType, interval: 1 } : null
        };

        try {
            const res = await api('/schedules', { method: 'POST', body: JSON.stringify(data) });
            if (!res.ok) throw new Error('Failed to create schedule');

            this.closeForm();
            await this.loadSchedules();
        } catch (err) {
            alert('Error: ' + err.message);
        }
    },

    async deleteSchedule(id) {
        if (!confirm('Delete this schedule?')) return;

        try {
            await api(`/schedules/${id}`, { method: 'DELETE' });
            await this.loadSchedules();
        } catch (err) {
            alert('Error: ' + err.message);
        }
    },

    destroy() {}
};
