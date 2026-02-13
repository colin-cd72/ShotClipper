// Overlays page - logo upload, lower-third config

const OverlaysPage = {
    overlays: [],

    async render(container) {
        container.innerHTML = `
            <h2 style="margin-bottom:16px">Overlay Settings</h2>

            <div class="grid grid-2">
                <div class="card">
                    <div class="card-header"><h3>Logo Bug</h3></div>
                    <div id="logo-drop" style="border:2px dashed var(--border);border-radius:var(--radius);padding:32px;text-align:center;cursor:pointer;margin-bottom:16px">
                        <p style="color:var(--text-secondary)">Drag & drop logo image here or click to upload</p>
                        <input type="file" id="logo-file" accept="image/*" style="display:none">
                    </div>
                    <div id="logo-preview" style="margin-bottom:16px"></div>
                    <form id="logo-form">
                        <div class="grid grid-2">
                            <div>
                                <label>Position X (%)</label>
                                <input type="number" id="logo-x" value="5" min="0" max="100">
                            </div>
                            <div>
                                <label>Position Y (%)</label>
                                <input type="number" id="logo-y" value="5" min="0" max="100">
                            </div>
                        </div>
                        <label>Scale (%)</label>
                        <input type="number" id="logo-scale" value="100" min="10" max="200">
                        <label>Opacity</label>
                        <input type="range" id="logo-opacity" min="0" max="1" step="0.1" value="1" style="width:100%">
                        <button type="submit" class="btn btn-primary" style="margin-top:8px">Save Logo Settings</button>
                    </form>
                </div>

                <div class="card">
                    <div class="card-header"><h3>Lower Third</h3></div>
                    <form id="lower-third-form">
                        <label>Font Family</label>
                        <select id="lt-font">
                            <option value="Arial">Arial</option>
                            <option value="Helvetica">Helvetica</option>
                            <option value="Inter">Inter</option>
                            <option value="Roboto">Roboto</option>
                        </select>
                        <label>Font Size (px)</label>
                        <input type="number" id="lt-size" value="48" min="12" max="120">
                        <label>Text Color</label>
                        <input type="color" id="lt-color" value="#ffffff">
                        <label>Background Color</label>
                        <input type="color" id="lt-bg-color" value="#000000">
                        <label>Background Opacity</label>
                        <input type="range" id="lt-bg-opacity" min="0" max="1" step="0.1" value="0.7" style="width:100%">
                        <label>Position</label>
                        <select id="lt-position">
                            <option value="bottom-left">Bottom Left</option>
                            <option value="bottom-center">Bottom Center</option>
                            <option value="bottom-right">Bottom Right</option>
                            <option value="top-left">Top Left</option>
                        </select>
                        <button type="submit" class="btn btn-primary" style="margin-top:8px">Save Lower Third</button>
                    </form>
                </div>
            </div>
        `;

        this.setupLogoUpload();
        this.setupForms();
        await this.loadOverlays();
    },

    setupLogoUpload() {
        const dropZone = document.getElementById('logo-drop');
        const fileInput = document.getElementById('logo-file');

        dropZone.addEventListener('click', () => fileInput.click());

        dropZone.addEventListener('dragover', (e) => {
            e.preventDefault();
            dropZone.style.borderColor = 'var(--accent-blue)';
        });

        dropZone.addEventListener('dragleave', () => {
            dropZone.style.borderColor = 'var(--border)';
        });

        dropZone.addEventListener('drop', (e) => {
            e.preventDefault();
            dropZone.style.borderColor = 'var(--border)';
            const file = e.dataTransfer.files[0];
            if (file) this.uploadLogo(file);
        });

        fileInput.addEventListener('change', () => {
            const file = fileInput.files[0];
            if (file) this.uploadLogo(file);
        });
    },

    async uploadLogo(file) {
        const formData = new FormData();
        formData.append('logo', file);

        try {
            const res = await fetch('/api/overlays/logo', {
                method: 'POST',
                headers: { 'Authorization': `Bearer ${Auth.token}` },
                body: formData
            });

            if (res.ok) {
                const data = await res.json();
                const preview = document.getElementById('logo-preview');
                preview.innerHTML = `<p style="color:var(--accent-green)">Logo uploaded: ${data.fileName}</p>`;
            }
        } catch (err) {
            alert('Upload failed: ' + err.message);
        }
    },

    setupForms() {
        document.getElementById('logo-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const config = {
                name: 'Logo Bug',
                configJson: JSON.stringify({
                    x: parseInt(document.getElementById('logo-x').value),
                    y: parseInt(document.getElementById('logo-y').value),
                    scale: parseInt(document.getElementById('logo-scale').value),
                    opacity: parseFloat(document.getElementById('logo-opacity').value)
                })
            };

            try {
                await api('/overlays/logo_bug', { method: 'PUT', body: JSON.stringify(config) });
                alert('Logo settings saved');
            } catch (err) {
                alert('Error: ' + err.message);
            }
        });

        document.getElementById('lower-third-form').addEventListener('submit', async (e) => {
            e.preventDefault();
            const config = {
                name: 'Lower Third',
                configJson: JSON.stringify({
                    font: document.getElementById('lt-font').value,
                    fontSize: parseInt(document.getElementById('lt-size').value),
                    color: document.getElementById('lt-color').value,
                    bgColor: document.getElementById('lt-bg-color').value,
                    bgOpacity: parseFloat(document.getElementById('lt-bg-opacity').value),
                    position: document.getElementById('lt-position').value
                })
            };

            try {
                await api('/overlays/lower_third', { method: 'PUT', body: JSON.stringify(config) });
                alert('Lower third settings saved');
            } catch (err) {
                alert('Error: ' + err.message);
            }
        });
    },

    async loadOverlays() {
        try {
            const res = await api('/overlays');
            if (res.ok) {
                this.overlays = await res.json();
                // Populate forms with existing config if available
                this.overlays.forEach(o => {
                    try {
                        const config = JSON.parse(o.configJson || '{}');
                        if (o.type === 'logo_bug') {
                            if (config.x != null) document.getElementById('logo-x').value = config.x;
                            if (config.y != null) document.getElementById('logo-y').value = config.y;
                            if (config.scale != null) document.getElementById('logo-scale').value = config.scale;
                            if (config.opacity != null) document.getElementById('logo-opacity').value = config.opacity;
                        } else if (o.type === 'lower_third') {
                            if (config.font) document.getElementById('lt-font').value = config.font;
                            if (config.fontSize) document.getElementById('lt-size').value = config.fontSize;
                            if (config.color) document.getElementById('lt-color').value = config.color;
                            if (config.bgColor) document.getElementById('lt-bg-color').value = config.bgColor;
                            if (config.bgOpacity != null) document.getElementById('lt-bg-opacity').value = config.bgOpacity;
                            if (config.position) document.getElementById('lt-position').value = config.position;
                        }
                    } catch {}
                });
            }
        } catch {}
    },

    destroy() {}
};
