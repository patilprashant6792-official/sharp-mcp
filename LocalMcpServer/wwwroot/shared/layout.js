/**
 * layout.js — shared shell renderer
 * Injects sidebar + toast container into every page.
 * To add a new nav item: add one entry to NAV_ITEMS.
 */

'use strict';

// ── Nav registry — extend here to add new pages ──────────────
const NAV_ITEMS = [
    { label: 'Projects',     icon: '&#128194;', href: '/features/projects/projects.html',         tooltip: 'Projects' },
    { label: 'Environments', icon: '&#127760;', href: '/features/environments/environments.html', tooltip: 'Environments' },
    { label: 'NuGet Cache',  icon: '&#128230;', href: '/features/nuget-cache/nuget-cache.html',   tooltip: 'NuGet Cache' },
];
// ── Sidebar collapse state (persisted) ───────────────────────
const COLLAPSE_KEY = 'mcp_sidebar_collapsed';

function isSidebarCollapsed() {
    return localStorage.getItem(COLLAPSE_KEY) === '1';
}

function setSidebarCollapsed(val) {
    localStorage.setItem(COLLAPSE_KEY, val ? '1' : '0');
}

// ── Active nav detection ──────────────────────────────────────
function isActiveHref(href) {
    const current = window.location.pathname.replace(/\/+$/, '') || '/';
    const target  = href.replace(/\/+$/, '');
    return current === target || current.endsWith(target);
}

// ── Build sidebar HTML ────────────────────────────────────────
function buildSidebarHTML() {
    const navItemsHTML = NAV_ITEMS.map(item => {
        const active = isActiveHref(item.href) ? 'active' : '';
        return `
            <a href="${item.href}" class="nav-item ${active}" data-tooltip="${item.tooltip}">
                <span class="nav-icon">${item.icon}</span>
                <span class="nav-label">${item.label}</span>
            </a>`;
    }).join('');

    return `
        <aside class="sidebar" id="app-sidebar">
            <div class="sidebar-brand">
                <div class="brand-icon">&#9889;</div>
                <div class="brand-text">
                    <span class="brand-name">dotnet-mcp</span>
                    <span class="brand-sub">server</span>
                </div>
                <button class="sidebar-collapse-btn" id="sidebar-collapse-btn" title="Toggle sidebar">&#9664;</button>
            </div>
            <div class="nav-section-label">Navigation</div>
            <nav class="sidebar-nav">
                ${navItemsHTML}
            </nav>
            <div class="sidebar-footer">
                <div class="sidebar-status">
                    <span class="status-dot"></span>
                    <span class="sidebar-status-text">Server Online</span>
                </div>
                <a class="sidebar-credit" href="https://github.com/patilprashant6792-official" target="_blank" rel="noopener">
                    <span class="credit-label">patilprashant6792-official</span>
                </a>
            </div>
        </aside>`;
}

// ── Inject layout ─────────────────────────────────────────────
function initLayout() {
    // Build shell if page uses .app-layout
    const appLayout = document.querySelector('.app-layout');
    if (!appLayout) return;

    // Inject sidebar as first child
    appLayout.insertAdjacentHTML('afterbegin', buildSidebarHTML());

    // Inject toast container at body level
    if (!document.getElementById('toast-container')) {
        document.body.insertAdjacentHTML('beforeend', '<div id="toast-container"></div>');
    }

    // Apply persisted collapse state
    const sidebar = document.getElementById('app-sidebar');
    if (isSidebarCollapsed()) sidebar.classList.add('collapsed');

    // Wire collapse button
    document.getElementById('sidebar-collapse-btn').addEventListener('click', () => {
        const collapsed = sidebar.classList.toggle('collapsed');
        setSidebarCollapsed(collapsed);
    });
}

// Run before DOMContentLoaded completes
document.addEventListener('DOMContentLoaded', initLayout);
