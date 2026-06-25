'use strict';

// ── State ─────────────────────────────────────────────────────
let allProjects    = [];        // ProjectConfigEntry[]
let selectedProjId = null;      // currently active project
let envMap         = {};        // projectId -> ProjectEnvironment[]
let openCards      = new Set(); // envId's that are expanded

// ── Init ──────────────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', async () => {
    await loadProjects();
});

// ── Data loading ───────────────────────────────────────────────
async function loadProjects() {
    try {
        const res  = await fetch('/api/projects');
        if (!res.ok) throw new Error('Failed to load projects');
        const data = await res.json();
        allProjects = data.projects || [];
        renderProjectList();
        updateStats();

        // Auto-select first project
        if (allProjects.length > 0) selectProject(allProjects[0].id);
    } catch (err) {
        McpUI.showError(err.message);
    }
}

async function loadEnvironments(projectId) {
    try {
        const res  = await fetch(`/api/projects/${projectId}/environments`);
        if (!res.ok) throw new Error('Failed to load environments');
        const data = await res.json();
        envMap[projectId] = data.environments || [];
        updateStats();
        return envMap[projectId];
    } catch (err) {
        McpUI.showError(err.message);
        return [];
    }
}

// ── Project panel ──────────────────────────────────────────────
function renderProjectList() {
    const el = document.getElementById('project-list');
    if (!el) return;

    if (allProjects.length === 0) {
        el.innerHTML = `
            <div class="empty-state" style="padding:40px 12px">
                <div class="empty-icon">&#128193;</div>
                <p class="empty-title">No projects</p>
                <p class="empty-hint">Add a project first</p>
            </div>`;
        return;
    }

    el.innerHTML = allProjects.map((p, i) => {
        const count   = envMap[p.id]?.length ?? 0;
        const isActive = p.id === selectedProjId;
        return `
        <div class="project-pill ${isActive ? 'active' : ''}"
             onclick="selectProject('${McpUI.escapeHtml(p.id)}')"
             style="animation: card-in 0.25s ease ${i * 40}ms both">
            <div class="pill-icon">&#128194;</div>
            <div class="pill-info">
                <span class="pill-name">${McpUI.escapeHtml(p.name)}</span>
                <span class="pill-env-count">${count} environment${count !== 1 ? 's' : ''}</span>
            </div>
            ${count > 0 ? `<div class="pill-badge">${count}</div>` : ''}
        </div>`;
    }).join('');
}

function selectProject(projectId) {
    selectedProjId = projectId;
    renderProjectList(); // re-render to update active state
    renderEnvPanel();
    loadEnvironments(projectId).then(() => renderEnvPanel());
}

// ── Environment panel ──────────────────────────────────────────
function renderEnvPanel() {
    const panel = document.getElementById('env-panel');
    if (!panel) return;

    if (!selectedProjId) {
        panel.innerHTML = `
            <div class="env-placeholder" id="env-placeholder">
                <div class="placeholder-art">&#9881;</div>
                <p class="placeholder-title">Select a project</p>
                <p class="placeholder-hint">Pick a project on the left to manage its HTTP environments</p>
            </div>`;
        return;
    }

    const project = allProjects.find(p => p.id === selectedProjId);
    const envs    = envMap[selectedProjId] ?? [];

    const envsHtml = envs.length === 0
        ? `<div class="empty-state" style="padding:60px 20px">
               <div class="empty-icon">&#127760;</div>
               <p class="empty-title">No environments yet</p>
               <p class="empty-hint">Add one below to start configuring HTTP access for Claude</p>
           </div>`
        : `<div class="env-list">${envs.map((e, i) => buildEnvCard(e, i)).join('')}</div>`;

    panel.innerHTML = `
        <div class="env-panel-header">
            <div class="env-panel-project">
                <span class="env-panel-project-name">&#9889; ${McpUI.escapeHtml(project?.name ?? '')}</span>
                <span class="env-panel-subtitle">Manage HTTP environments for this project</span>
            </div>
            <button class="btn btn-primary btn-sm" onclick="openAddForm()">&#43; Add Environment</button>
        </div>
        ${envsHtml}
        <div id="add-env-slot"></div>`;

    // Re-open previously open cards
    envs.forEach(e => { if (openCards.has(e.id)) toggleCard(e.id, true); });
}

// ── Build environment card HTML ────────────────────────────────
function buildEnvCard(env, idx) {
    const authChip  = authChipHtml(env.auth?.type ?? 'None');
    const isDefault = env.isDefault;
    const isOpen    = openCards.has(env.id);

    return `
    <div class="env-card ${isDefault ? 'is-default' : ''} ${isOpen ? 'open' : ''}"
         id="envcard-${McpUI.escapeHtml(env.id)}"
         style="animation-delay:${idx * 50}ms">

        <div class="env-card-header" onclick="toggleCard('${McpUI.escapeHtml(env.id)}')">
            <div class="env-status-dot"></div>
            <div class="env-name-group">
                <div class="env-card-name">${McpUI.escapeHtml(env.name)}</div>
                <div class="env-card-meta">
                    <span class="env-url-preview">${McpUI.escapeHtml(env.baseUrl)}</span>
                    ${authChip}
                    ${env.skipTlsVerify ? '<span class="env-auth-chip auth-chip-none" title="TLS verify disabled">No TLS</span>' : ''}
                </div>
            </div>
            ${isDefault ? '<span class="default-crown">&#11088; Default</span>' : ''}
            <div class="env-card-actions" onclick="event.stopPropagation()">
                ${!isDefault ? `<button class="btn btn-ghost btn-sm" onclick="setDefault('${McpUI.escapeHtml(env.id)}')">Set Default</button>` : ''}
                <button class="btn btn-danger btn-sm" onclick="deleteEnv('${McpUI.escapeHtml(env.id)}')">&#128465;</button>
            </div>
            <span class="env-chevron">&#9660;</span>
        </div>

        <div class="env-card-body">
            ${buildEnvForm(env)}
        </div>
    </div>`;
}

// ── Environment form (inside card) ────────────────────────────
function buildEnvForm(env) {
    const authType = env.auth?.type ?? 'None';
    return `
    <div class="env-form" id="form-${McpUI.escapeHtml(env.id)}">

        <div class="env-field">
            <span class="env-field-label">Name</span>
            <div class="env-field-control">
                <input class="env-input mono" type="text"
                    id="f-name-${McpUI.escapeHtml(env.id)}"
                    value="${McpUI.escapeHtml(env.name)}"
                    placeholder="e.g. local, staging, prod">
            </div>
        </div>

        <div class="env-field">
            <span class="env-field-label">Base URL</span>
            <div class="env-field-control">
                <input class="env-input mono" type="text"
                    id="f-url-${McpUI.escapeHtml(env.id)}"
                    value="${McpUI.escapeHtml(env.baseUrl)}"
                    placeholder="https://localhost:7001">
            </div>
        </div>

        <div class="env-field tls-row">
            <span class="env-field-label">Options</span>
            <div class="env-field-control">
                <label class="tls-toggle">
                    <input type="checkbox" id="f-tls-${McpUI.escapeHtml(env.id)}" ${env.skipTlsVerify ? 'checked' : ''}>
                    <span class="tls-check">&#10003;</span>
                    <span class="tls-hint">Skip TLS verify (local self-signed certs)</span>
                </label>
            </div>
        </div>

        <div class="env-field">
            <span class="env-field-label">Auth</span>
            <div class="env-field-control">
                <div class="auth-type-group" id="auth-tabs-${McpUI.escapeHtml(env.id)}">
                    ${['None','Bearer','Basic'].map(t =>
                        `<button class="auth-type-btn ${authType === t ? 'active' : ''}"
                            onclick="switchAuthTab('${McpUI.escapeHtml(env.id)}','${t}')">${t}</button>`
                    ).join('')}
                </div>
            </div>
        </div>

        <div id="auth-fields-${McpUI.escapeHtml(env.id)}">
            ${buildAuthFields(env.id, authType, env.auth)}
        </div>

        <div class="env-form-footer">
            <div class="env-form-actions">
                <button class="btn btn-primary btn-sm" onclick="saveEnv('${McpUI.escapeHtml(env.id)}')">
                    &#10003; Save
                </button>
                <button class="btn btn-ghost btn-sm" onclick="testEnv('${McpUI.escapeHtml(env.id)}')">
                    &#9654; Test
                </button>
            </div>
            <div class="test-result" id="test-result-${McpUI.escapeHtml(env.id)}"></div>
        </div>
    </div>`;
}

// ── Auth field panels ─────────────────────────────────────────
function buildAuthFields(envId, type, auth) {
    const id = McpUI.escapeHtml(envId);
    if (type === 'Bearer') {
        return `
        <div class="auth-fields">
            <div class="auth-field-row">
                <span class="auth-field-label">Token</span>
                <div class="token-wrap">
                    <input class="env-input mono token-input" type="password"
                        id="f-bearer-${id}"
                        value="${McpUI.escapeHtml(auth?.bearer?.token ?? '')}"
                        placeholder="Bearer token">
                    <button class="token-reveal-btn" type="button"
                        onclick="toggleReveal('f-bearer-${id}', this)">&#128065;</button>
                </div>
            </div>
        </div>`;
    }
    if (type === 'Basic') {
        return `
        <div class="auth-fields">
            <div class="auth-field-row">
                <span class="auth-field-label">Username</span>
                <input class="env-input mono" type="text"
                    id="f-basic-user-${id}"
                    value="${McpUI.escapeHtml(auth?.basic?.username ?? '')}"
                    placeholder="username" autocomplete="off">
            </div>
            <div class="auth-field-row">
                <span class="auth-field-label">Password</span>
                <div class="token-wrap">
                    <input class="env-input mono token-input" type="password"
                        id="f-basic-pass-${id}"
                        value="${McpUI.escapeHtml(auth?.basic?.password ?? '')}"
                        placeholder="password" autocomplete="off">
                    <button class="token-reveal-btn" type="button"
                        onclick="toggleReveal('f-basic-pass-${id}', this)">&#128065;</button>
                </div>
            </div>
        </div>`;
    }
    return ''; // None — no auth fields
}

// ── Auth tab switch ────────────────────────────────────────────
function switchAuthTab(envId, type) {
    const id     = McpUI.escapeHtml(envId);
    const tabs   = document.getElementById(`auth-tabs-${id}`);
    const fields = document.getElementById(`auth-fields-${id}`);
    if (!tabs || !fields) return;

    tabs.querySelectorAll('.auth-type-btn').forEach(btn => {
        btn.classList.toggle('active', btn.textContent.trim() === type);
    });

    fields.innerHTML = buildAuthFields(envId, type, {});
}

// ── Card toggle ────────────────────────────────────────────────
function toggleCard(envId, forceOpen) {
    const card = document.getElementById(`envcard-${envId}`);
    if (!card) return;
    const shouldOpen = forceOpen ?? !card.classList.contains('open');
    card.classList.toggle('open', shouldOpen);
    if (shouldOpen) openCards.add(envId); else openCards.delete(envId);
}

// ── Token reveal ───────────────────────────────────────────────
function toggleReveal(inputId, btn) {
    const input = document.getElementById(inputId);
    if (!input) return;
    const isHidden = input.type === 'password';
    input.type = isHidden ? 'text' : 'password';
    btn.textContent = isHidden ? '&#128683;' : '&#128065;';
    btn.innerHTML   = isHidden ? '&#128683;' : '&#128065;';
}

// ── Collect form values for a card ────────────────────────────
function collectFormData(envId) {
    const id       = McpUI.escapeHtml(envId);
    const name     = document.getElementById(`f-name-${id}`)?.value.trim();
    const baseUrl  = document.getElementById(`f-url-${id}`)?.value.trim();
    const skipTls  = document.getElementById(`f-tls-${id}`)?.checked ?? false;

    // Detect active auth tab
    const tabs    = document.getElementById(`auth-tabs-${id}`);
    const active  = tabs?.querySelector('.auth-type-btn.active')?.textContent.trim() ?? 'None';

    let auth = { type: active };
    if (active === 'Bearer') {
        auth.bearer = { token: document.getElementById(`f-bearer-${id}`)?.value ?? '' };
    } else if (active === 'Basic') {
        auth.basic = {
            username: document.getElementById(`f-basic-user-${id}`)?.value ?? '',
            password: document.getElementById(`f-basic-pass-${id}`)?.value ?? '',
        };
    }

    return { name, baseUrl, skipTlsVerify: skipTls, auth };
}

// ── Save existing env ──────────────────────────────────────────
async function saveEnv(envId) {
    const payload = collectFormData(envId);
    if (!payload.name || !payload.baseUrl) {
        McpUI.showError('Name and Base URL are required'); return;
    }

    // Preserve isDefault from current state
    const current  = (envMap[selectedProjId] ?? []).find(e => e.id === envId);
    payload.isDefault = current?.isDefault ?? false;

    try {
        const res = await fetch(`/api/projects/${selectedProjId}/environments/${envId}`, {
            method:  'PUT',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(payload),
        });
        if (!res.ok) { const e = await res.json(); throw new Error(e.error); }

        McpUI.showSuccess(`Environment '${payload.name}' saved`);
        await loadEnvironments(selectedProjId);
        renderEnvPanel();
        renderProjectList();
        // Keep card open after save
        openCards.add(envId);
        toggleCard(envId, true);
    } catch (err) {
        McpUI.showError(err.message);
    }
}

// ── Test connectivity ──────────────────────────────────────────
async function testEnv(envId) {
    const resultEl = document.getElementById(`test-result-${McpUI.escapeHtml(envId)}`);
    if (!resultEl) return;

    const payload = collectFormData(envId);
    if (!payload.baseUrl) { McpUI.showError('Enter a Base URL first'); return; }

    // Show pending
    setTestResult(resultEl, 'pending', '&#9203; Testing...');

    try {
        const res  = await fetch('/api/environments/probe', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(payload),
        });
        const data = await res.json();

        if (data.reachable) {
            setTestResult(resultEl, 'ok', `&#10003; ${data.status} &middot; ${data.durationMs}ms`);
        } else {
            setTestResult(resultEl, 'err', `&#10007; ${McpUI.escapeHtml(data.error ?? 'Unreachable')}`);
        }
    } catch (err) {
        setTestResult(resultEl, 'err', `&#10007; ${McpUI.escapeHtml(err.message)}`);
    }
}

function setTestResult(el, type, html) {
    el.className = `test-result ${type}`;
    el.innerHTML = `<span class="test-dot"></span><span>${html}</span>`;
    el.classList.add('visible');
    if (type !== 'pending') setTimeout(() => el.classList.remove('visible'), 6000);
}

// ── Delete env ─────────────────────────────────────────────────
async function deleteEnv(envId) {
    const env = (envMap[selectedProjId] ?? []).find(e => e.id === envId);
    const ok  = await McpUI.confirmAsync(`Delete environment '${env?.name ?? envId}'?`);
    if (!ok) return;

    try {
        const res = await fetch(`/api/projects/${selectedProjId}/environments/${envId}`, { method: 'DELETE' });
        if (!res.ok) { const e = await res.json(); throw new Error(e.error); }
        McpUI.showSuccess('Environment deleted');
        openCards.delete(envId);
        await loadEnvironments(selectedProjId);
        renderEnvPanel();
        renderProjectList();
    } catch (err) {
        McpUI.showError(err.message);
    }
}

// ── Set default ────────────────────────────────────────────────
async function setDefault(envId) {
    try {
        const res = await fetch(`/api/projects/${selectedProjId}/environments/${envId}/set-default`, { method: 'POST' });
        if (!res.ok) { const e = await res.json(); throw new Error(e.error); }
        McpUI.showSuccess('Default environment updated');
        await loadEnvironments(selectedProjId);
        renderEnvPanel();
    } catch (err) {
        McpUI.showError(err.message);
    }
}

// ── Add new environment (inline form at bottom) ────────────────
function openAddForm() {
    const slot = document.getElementById('add-env-slot');
    if (!slot || slot.querySelector('.new-env-form')) return; // already open

    slot.innerHTML = buildNewEnvForm();
}

function buildNewEnvForm() {
    return `
    <div class="env-card open new-env-form" style="border-color: rgba(88,166,255,0.4);">
        <div class="env-card-header" style="cursor:default">
            <div class="env-status-dot" style="background:var(--accent)"></div>
            <div class="env-name-group">
                <div class="env-card-name" style="color:var(--accent)">New Environment</div>
            </div>
            <div class="env-card-actions">
                <button class="btn btn-ghost btn-sm" onclick="cancelAdd()">Cancel</button>
            </div>
        </div>
        <div class="env-card-body" style="max-height:600px; border-top-color:var(--border-subtle)">
            <div class="env-form">

                <div class="env-field">
                    <span class="env-field-label">Name</span>
                    <div class="env-field-control">
                        <input class="env-input mono" type="text" id="new-name" placeholder="e.g. local, staging, prod" autofocus>
                    </div>
                </div>

                <div class="env-field">
                    <span class="env-field-label">Base URL</span>
                    <div class="env-field-control">
                        <input class="env-input mono" type="text" id="new-url" placeholder="https://localhost:7001">
                    </div>
                </div>

                <div class="env-field tls-row">
                    <span class="env-field-label">Options</span>
                    <div class="env-field-control">
                        <label class="tls-toggle">
                            <input type="checkbox" id="new-tls">
                            <span class="tls-check">&#10003;</span>
                            <span class="tls-hint">Skip TLS verify (local self-signed certs)</span>
                        </label>
                    </div>
                </div>

                <div class="env-field">
                    <span class="env-field-label">Auth</span>
                    <div class="env-field-control">
                        <div class="auth-type-group" id="new-auth-tabs">
                            <button class="auth-type-btn active" onclick="switchNewAuthTab('None')">None</button>
                            <button class="auth-type-btn" onclick="switchNewAuthTab('Bearer')">Bearer</button>
                            <button class="auth-type-btn" onclick="switchNewAuthTab('Basic')">Basic</button>
                        </div>
                    </div>
                </div>

                <div id="new-auth-fields"></div>

                <div class="env-form-footer">
                    <div class="env-form-actions">
                        <button class="btn btn-primary btn-sm" onclick="submitNewEnv()">&#10003; Create Environment</button>
                    </div>
                </div>
            </div>
        </div>
    </div>`;
}

function switchNewAuthTab(type) {
    document.querySelectorAll('#new-auth-tabs .auth-type-btn').forEach(btn => {
        btn.classList.toggle('active', btn.textContent.trim() === type);
    });
    document.getElementById('new-auth-fields').innerHTML = buildNewAuthFields(type);
}

function buildNewAuthFields(type) {
    if (type === 'Bearer') return `
        <div class="auth-fields">
            <div class="auth-field-row">
                <span class="auth-field-label">Token</span>
                <div class="token-wrap">
                    <input class="env-input mono token-input" type="password" id="new-bearer" placeholder="Bearer token">
                    <button class="token-reveal-btn" type="button" onclick="toggleReveal('new-bearer', this)">&#128065;</button>
                </div>
            </div>
        </div>`;
    if (type === 'Basic') return `
        <div class="auth-fields">
            <div class="auth-field-row">
                <span class="auth-field-label">Username</span>
                <input class="env-input mono" type="text" id="new-user" placeholder="username" autocomplete="off">
            </div>
            <div class="auth-field-row">
                <span class="auth-field-label">Password</span>
                <div class="token-wrap">
                    <input class="env-input mono token-input" type="password" id="new-pass" placeholder="password">
                    <button class="token-reveal-btn" type="button" onclick="toggleReveal('new-pass', this)">&#128065;</button>
                </div>
            </div>
        </div>`;
    return '';
}

async function submitNewEnv() {
    const name    = document.getElementById('new-name')?.value.trim();
    const baseUrl = document.getElementById('new-url')?.value.trim();
    const skipTls = document.getElementById('new-tls')?.checked ?? false;

    const tabs   = document.getElementById('new-auth-tabs');
    const active = tabs?.querySelector('.auth-type-btn.active')?.textContent.trim() ?? 'None';

    let auth = { type: active };
    if (active === 'Bearer')  auth.bearer = { token: document.getElementById('new-bearer')?.value ?? '' };
    if (active === 'Basic')   auth.basic  = { username: document.getElementById('new-user')?.value ?? '', password: document.getElementById('new-pass')?.value ?? '' };

    if (!name || !baseUrl) { McpUI.showError('Name and Base URL are required'); return; }

    try {
        const res = await fetch(`/api/projects/${selectedProjId}/environments`, {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify({ name, baseUrl, skipTlsVerify: skipTls, isDefault: false, auth }),
        });
        if (!res.ok) { const e = await res.json(); throw new Error(e.error); }
        const newEnv = await res.json();
        McpUI.showSuccess(`Environment '${name}' created`);
        openCards.add(newEnv.id);
        await loadEnvironments(selectedProjId);
        renderEnvPanel();
        renderProjectList();
    } catch (err) {
        McpUI.showError(err.message);
    }
}

function cancelAdd() {
    const slot = document.getElementById('add-env-slot');
    if (slot) slot.innerHTML = '';
}

// ── Auth chip helper ───────────────────────────────────────────
function authChipHtml(type) {
    const map = {
        None:   ['auth-chip-none',   'No Auth'],
        Bearer: ['auth-chip-bearer', 'Bearer'],
        Basic:  ['auth-chip-basic',  'Basic'],
    };
    const [cls, label] = map[type] ?? map.None;
    return `<span class="env-auth-chip ${cls}">${label}</span>`;
}

// ── Stats ──────────────────────────────────────────────────────
function updateStats() {
    const totalEnvs = Object.values(envMap).reduce((s, arr) => s + arr.length, 0);
    const withAuth  = Object.values(envMap).reduce((s, arr) =>
        s + arr.filter(e => e.auth?.type && e.auth.type !== 'None').length, 0);

    const s = (id, v) => { const el = document.getElementById(id); if (el) el.textContent = v; };
    s('stat-projects', allProjects.length);
    s('stat-envs',     totalEnvs);
    s('stat-auth',     withAuth);
}
