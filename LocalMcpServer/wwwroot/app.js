// State
let projects = [];
let editingProjectId = null;

// DOM Elements
const projectsList = document.getElementById('projects-list');
const emptyState = document.getElementById('empty-state');
const projectCount = document.getElementById('project-count');
const addProjectBtn = document.getElementById('add-project-btn');
const modal = document.getElementById('project-modal');
const modalTitle = document.getElementById('modal-title');
const modalClose = document.getElementById('modal-close');
const modalCancel = document.getElementById('modal-cancel');
const modalSave = document.getElementById('modal-save');
const errorBanner = document.getElementById('error-banner');
const successBanner = document.getElementById('success-banner');

// Form fields
const projectName = document.getElementById('project-name');
const projectPath = document.getElementById('project-path');
const projectDescription = document.getElementById('project-description');
const projectEnabled = document.getElementById('project-enabled');
const validatePathBtn = document.getElementById('validate-path-btn');
const validationResult = document.getElementById('validation-result');

// Initialize
document.addEventListener('DOMContentLoaded', () => {
    loadProjects();
    attachEventListeners();
});

function attachEventListeners() {
    addProjectBtn.addEventListener('click', openAddModal);
    modalClose.addEventListener('click', closeModal);
    modalCancel.addEventListener('click', closeModal);
    modalSave.addEventListener('click', saveProject);
    validatePathBtn.addEventListener('click', validatePath);
}

// API calls
async function loadProjects() {
    try {
        const res = await fetch('/api/projects');
        if (!res.ok) throw new Error('Failed to load projects');

        const config = await res.json();
        projects = config.projects || [];
        renderProjects();
    } catch (err) {
        showError('Failed to load projects: ' + err.message);
    }
}

async function saveProject() {
    const name = projectName.value.trim();
    const path = projectPath.value.trim();
    const description = projectDescription.value.trim();
    const enabled = projectEnabled.checked;

    if (!name || !path) {
        showError('Project name and path are required');
        return;
    }

    const project = {
        id: editingProjectId || '',
        name,
        path,
        description,
        enabled
    };

    try {
        const url = editingProjectId
            ? `/api/projects/${editingProjectId}`
            : '/api/projects';

        const method = editingProjectId ? 'PUT' : 'POST';

        const res = await fetch(url, {
            method,
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(project)
        });

        if (!res.ok) {
            const error = await res.json();
            throw new Error(error.error || 'Failed to save project');
        }

        showSuccess(editingProjectId ? 'Project updated successfully' : 'Project added successfully');
        closeModal();
        loadProjects();
    } catch (err) {
        showError(err.message);
    }
}

async function deleteProject(id) {
    if (!confirm('Are you sure you want to delete this project?')) {
        return;
    }

    try {
        const res = await fetch(`/api/projects/${id}`, { method: 'DELETE' });

        if (!res.ok) {
            const error = await res.json();
            throw new Error(error.error || 'Failed to delete project');
        }

        showSuccess('Project deleted successfully');
        loadProjects();
    } catch (err) {
        showError(err.message);
    }
}

async function validatePath() {
    const path = projectPath.value.trim();

    if (!path) {
        showError('Please enter a path first');
        return;
    }

    validationResult.classList.remove('success', 'error', 'hidden');
    validationResult.innerHTML = '<p>⏳ Validating...</p>';

    try {
        const res = await fetch('/api/projects/validate', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path })
        });

        const result = await res.json();

        if (result.isValid) {
            validationResult.classList.add('success');
            const meta = result.metadata;
            validationResult.innerHTML = `
                <h4>✅ Valid Project Path</h4>
                <ul>
                    <li>Solution files: ${meta.hasSolutionFile ? meta.solutionFiles.join(', ') : 'None'}</li>
                    <li>Project files: ${meta.csprojCount} .csproj file(s)</li>
                    ${meta.detectedFramework ? `<li>Framework: ${meta.detectedFramework}</li>` : ''}
                </ul>
            `;
        } else {
            validationResult.classList.add('error');
            validationResult.innerHTML = `<h4>❌ Invalid Path</h4><p>${result.error}</p>`;
        }
    } catch (err) {
        validationResult.classList.add('error');
        validationResult.innerHTML = `<h4>❌ Validation Failed</h4><p>${err.message}</p>`;
    }
}

// UI functions
function renderProjects() {
    projectCount.textContent = projects.length;

    if (projects.length === 0) {
        projectsList.classList.add('hidden');
        emptyState.classList.remove('hidden');
        return;
    }

    projectsList.classList.remove('hidden');
    emptyState.classList.add('hidden');

    projectsList.innerHTML = projects.map(p => `
        <div class="project-card">
            <div class="project-header">
                <div class="project-info">
                    <h3>${escapeHtml(p.name)} <span class="status ${p.enabled ? 'enabled' : 'disabled'}">${p.enabled ? 'Enabled' : 'Disabled'}</span></h3>
                </div>
                <div class="project-actions">
                    <button class="btn btn-edit" onclick="openEditModal('${p.id}')">Edit</button>
                    <button class="btn btn-danger" onclick="deleteProject('${p.id}')">Delete</button>
                </div>
            </div>
            <div class="project-details">
                <p><strong>Path:</strong> <span class="path">${escapeHtml(p.path)}</span></p>
                ${p.description ? `<p><strong>Description:</strong> ${escapeHtml(p.description)}</p>` : ''}
                <p><strong>Added:</strong> ${new Date(p.addedDate).toLocaleDateString()}</p>
            </div>
        </div>
    `).join('');
}

function openAddModal() {
    editingProjectId = null;
    modalTitle.textContent = 'Add New Project';
    resetForm();
    modal.classList.remove('hidden');
}

function openEditModal(id) {
    const project = projects.find(p => p.id === id);
    if (!project) return;

    editingProjectId = id;
    modalTitle.textContent = 'Edit Project';

    projectName.value = project.name;
    projectPath.value = project.path;
    projectDescription.value = project.description || '';
    projectEnabled.checked = project.enabled;
    validationResult.classList.add('hidden');

    modal.classList.remove('hidden');
}

function closeModal() {
    modal.classList.add('hidden');
    resetForm();
}

function resetForm() {
    projectName.value = '';
    projectPath.value = '';
    projectDescription.value = '';
    projectEnabled.checked = true;
    validationResult.classList.add('hidden');
}

function showError(message) {
    errorBanner.textContent = message;
    errorBanner.classList.remove('hidden');
    successBanner.classList.add('hidden');
    setTimeout(() => errorBanner.classList.add('hidden'), 5000);
}

function showSuccess(message) {
    successBanner.textContent = message;
    successBanner.classList.remove('hidden');
    errorBanner.classList.add('hidden');
    setTimeout(() => successBanner.classList.add('hidden'), 3000);
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}