import {
  App,
  applyDocumentTheme,
  applyHostStyleVariables,
} from '@modelcontextprotocol/ext-apps';

interface CommitInfo {
  sha: string;
  shortSha: string;
  subject: string;
  date: string;
  author?: string;
}

interface DeployedInfo {
  sha: string;
  shortSha: string;
  subject: string;
  timestamp: string;
  success: boolean;
  error?: string | null;
}

interface PrInfo {
  number: number;
  title: string;
  headRefName: string;
  baseRefName: string;
  author?: { login?: string };
  url: string;
  isDraft?: boolean;
  mergeable?: string;
  mergeStateStatus?: string;
  additions?: number;
  deletions?: number;
  changedFiles?: number;
  updatedAt?: string;
  mergedAt?: string;
  statusCheckRollup?: Array<{ state?: string; conclusion?: string; status?: string }>;
}

interface WorktreeInfo {
  path: string;
  name: string;
  head: string;
  branch: string;
  isMain: boolean;
  isDirty: boolean;
  mergedIntoMain: boolean;
  openPrNumber: number | null;
  mergedPrNumber: number | null;
}

interface DeployJobSummary {
  jobId: string;
  running: boolean;
  success: boolean | null;
  phase: string;
  startedAt: string;
  finishedAt: string | null;
  error: string | null;
  lineCount: number;
}

interface StatusPayload {
  repoPath: string;
  currentBranch: string;
  isDirty: boolean;
  dirtyFiles: string[];
  localMain: CommitInfo | null;
  originMain: CommitInfo | null;
  deployed: DeployedInfo | null;
  needsDeploy: boolean;
  commitsBehindOrigin: CommitInfo[];
  commitsSinceDeploy: CommitInfo[];
  fetchError: string | null;
  ghAuth: { authenticated: boolean; message: string };
  prError: string | null;
  openPrs: PrInfo[];
  mergedPrs: PrInfo[];
  worktrees: WorktreeInfo[];
  deployJob: DeployJobSummary | null;
  history: DeployedInfo[];
}

interface PollPayload {
  found: boolean;
  jobId?: string;
  running: boolean;
  success?: boolean | null;
  phase?: string;
  error?: string | null;
  lines: string[];
  nextOffset: number;
}

const isStandalone = window.parent === window;
const mcpApp = isStandalone ? null : new App({ name: 'NetDaemon Deploy', version: '1.0.0' }, {});

let currentStatus: StatusPayload | null = null;
let busy = false;
let runTests = false;
let runCodegen = false;
let mergeMethods: Record<number, 'squash' | 'merge' | 'rebase'> = {};
let activeJobId: string | null = null;
let deployRunning = false;
let deployPhase = '';
let deploySuccess: boolean | null = null;
let deployLines: string[] = [];
let deployOffset = 0;
let pollTimer: number | null = null;
let toastMsg: { text: string; kind: 'ok' | 'err' } | null = null;
let toastTimer: number | null = null;
let pendingClosePr: number | null = null;
let pendingRemoveWt: string | null = null;

function showToast(text: string, kind: 'ok' | 'err' = 'ok') {
  toastMsg = { text, kind };
  if (toastTimer) window.clearTimeout(toastTimer);
  toastTimer = window.setTimeout(() => {
    toastMsg = null;
    render();
  }, 4500);
  render();
}

async function callBackend<T>(toolName: string, args: Record<string, unknown> = {}): Promise<T> {
  if (isStandalone) {
    if (toolName === 'app_get_status') {
      const r = await fetch(`/api/status?fetch=${args.fetch === false ? 'false' : 'true'}`);
      return (await r.json()) as T;
    }
    if (toolName === 'app_poll_deploy') {
      const qs = new URLSearchParams();
      if (args.jobId) qs.set('jobId', String(args.jobId));
      qs.set('offset', String(args.offset ?? 0));
      const r = await fetch(`/api/deploy/poll?${qs.toString()}`);
      return (await r.json()) as T;
    }
    const routeMap: Record<string, string> = {
      app_merge_pr: '/api/pr/merge',
      app_close_pr: '/api/pr/close',
      app_remove_worktree: '/api/worktree/remove',
      app_start_deploy: '/api/deploy/start',
    };
    const r = await fetch(routeMap[toolName], {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(args),
    });
    return (await r.json()) as T;
  }

  const res = await mcpApp!.callServerTool({ name: toolName, arguments: args });
  return (res.structuredContent ?? {}) as T;
}

function openExternal(url: string) {
  if (!isStandalone && mcpApp) {
    mcpApp.openLink({ url }).catch(() => window.open(url, '_blank'));
  } else {
    window.open(url, '_blank');
  }
}

function esc(s: unknown): string {
  return String(s ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function relTime(iso?: string | null): string {
  if (!iso) return '';
  const diff = Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 1000));
  if (diff < 60) return `${diff}s ago`;
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
  return `${Math.floor(diff / 86400)}d ago`;
}

function checkSummary(pr: PrInfo): { label: string; cls: string } | null {
  const checks = pr.statusCheckRollup ?? [];
  if (!checks.length) return null;
  const failed = checks.some(
    (c) => c.conclusion === 'FAILURE' || c.conclusion === 'ERROR' || c.state === 'FAILURE'
  );
  if (failed) return { label: 'Checks failed', cls: 'err' };
  const pending = checks.some((c) => c.status === 'IN_PROGRESS' || c.status === 'QUEUED' || c.state === 'PENDING');
  if (pending) return { label: 'Checks running', cls: 'warn' };
  return { label: 'Checks passed', cls: 'ok' };
}

async function refresh(fetchRemote = true) {
  busy = true;
  render();
  try {
    const status = await callBackend<StatusPayload>('app_get_status', { fetch: fetchRemote });
    applyStatus(status);
  } catch (e) {
    showToast(`Failed to refresh: ${String(e)}`, 'err');
  } finally {
    busy = false;
    render();
  }
}

function applyStatus(status: StatusPayload) {
  currentStatus = status;
  if (status.deployJob && status.deployJob.running && activeJobId !== status.deployJob.jobId) {
    activeJobId = status.deployJob.jobId;
    deployRunning = true;
    deployOffset = 0;
    deployLines = [];
    startPolling();
  }
}

async function triggerDeploy(mergePrNumber?: number) {
  if (deployRunning) return;
  const method = mergePrNumber ? mergeMethods[mergePrNumber] ?? 'squash' : 'squash';
  try {
    const res = await callBackend<{ ok: boolean; jobId?: string; error?: string }>('app_start_deploy', {
      runTests,
      runCodegen,
      ...(mergePrNumber ? { mergePrNumber, mergeMethod: method, deleteBranch: true } : {}),
    });
    if (!res.ok) {
      showToast(res.error || 'Could not start deploy', 'err');
      return;
    }
    activeJobId = res.jobId ?? null;
    deployRunning = true;
    deploySuccess = null;
    deployPhase = 'Starting';
    deployLines = [];
    deployOffset = 0;
    render();
    startPolling();
  } catch (e) {
    showToast(`Deploy failed to start: ${String(e)}`, 'err');
  }
}

function startPolling() {
  if (pollTimer) window.clearTimeout(pollTimer);
  const tick = async () => {
    try {
      const p = await callBackend<PollPayload>('app_poll_deploy', {
        jobId: activeJobId ?? undefined,
        offset: deployOffset,
      });
      if (p.found) {
        if (p.lines.length) {
          deployLines.push(...p.lines);
          deployOffset = p.nextOffset;
        }
        deployRunning = p.running;
        deployPhase = p.phase ?? '';
        deploySuccess = p.success ?? null;
        render();
        const logEl = document.getElementById('deploy-log');
        if (logEl) logEl.scrollTop = logEl.scrollHeight;
      }
      if (p.running) {
        pollTimer = window.setTimeout(tick, 500);
      } else {
        pollTimer = null;
        if (deploySuccess === true) showToast('Deploy completed successfully!', 'ok');
        else if (deploySuccess === false) showToast(`Deploy failed: ${p.error ?? 'see log'}`, 'err');
        await refresh(false);
      }
    } catch {
      pollTimer = window.setTimeout(tick, 1200);
    }
  };
  tick();
}

async function handleMergePr(prNumber: number) {
  const method = mergeMethods[prNumber] ?? 'squash';
  busy = true;
  render();
  try {
    const res = await callBackend<{ ok: boolean; error?: string }>('app_merge_pr', {
      number: prNumber,
      method,
      deleteBranch: true,
    });
    if (!res.ok) showToast(res.error || `Failed to merge PR #${prNumber}`, 'err');
    else showToast(`Merged PR #${prNumber} (${method})`, 'ok');
  } catch (e) {
    showToast(String(e), 'err');
  } finally {
    busy = false;
    await refresh(true);
  }
}

async function handleClosePr(prNumber: number) {
  pendingClosePr = null;
  busy = true;
  render();
  try {
    const res = await callBackend<{ ok: boolean; error?: string }>('app_close_pr', { number: prNumber });
    if (!res.ok) showToast(res.error || `Failed to close PR #${prNumber}`, 'err');
    else showToast(`Closed PR #${prNumber}`, 'ok');
  } catch (e) {
    showToast(String(e), 'err');
  } finally {
    busy = false;
    await refresh(false);
  }
}

async function handleRemoveWorktree(wt: WorktreeInfo) {
  pendingRemoveWt = null;
  busy = true;
  render();
  try {
    const res = await callBackend<{ ok: boolean; output?: string; error?: string }>('app_remove_worktree', {
      path: wt.path,
      force: wt.isDirty || !wt.mergedIntoMain,
      deleteBranch: true,
    });
    if (!res.ok) showToast(res.error || 'Failed to remove worktree', 'err');
    else showToast(res.output || `Removed ${wt.name}`, 'ok');
  } catch (e) {
    showToast(String(e), 'err');
  } finally {
    busy = false;
    await refresh(false);
  }
}

function render() {
  const root = document.getElementById('app');
  if (!root) return;

  if (!currentStatus) {
    root.innerHTML = `<div class="empty">Loading NetDaemon Deploy dashboard...</div>`;
    return;
  }

  const s = currentStatus;
  const deployed = s.deployed;
  const upToDate = !s.needsDeploy && Boolean(deployed);

  const worktreeByBranch = new Map<string, WorktreeInfo>();
  for (const w of s.worktrees) {
    if (!w.isMain) worktreeByBranch.set(w.branch, w);
  }

  let banners = '';
  if (!s.ghAuth.authenticated) {
    banners += `
      <div class="banner warn">
        <div>
          <strong>GitHub CLI not logged in.</strong> Run <code class="mono">gh auth login</code> in your terminal to manage Pull Requests here. Local deploy &amp; worktree tools still work.
        </div>
      </div>`;
  }
  if (s.currentBranch !== 'main' || s.isDirty) {
    banners += `
      <div class="banner error">
        <div>
          <strong>Primary repo needs attention:</strong>
          ${s.currentBranch !== 'main' ? `Currently on branch <code class="mono">${esc(s.currentBranch)}</code> (must be <code class="mono">main</code>). ` : ''}
          ${s.isDirty ? `${s.dirtyFiles.length} uncommitted change(s) in <code class="mono">${esc(s.repoPath)}</code>.` : ''}
        </div>
      </div>`;
  } else if (s.needsDeploy) {
    const pendingHtml = s.commitsSinceDeploy.length
      ? `<ul class="commit-list">${s.commitsSinceDeploy
          .slice(0, 5)
          .map((c) => `<li><span class="mono">${esc(c.shortSha)}</span> ${esc(c.subject)}</li>`)
          .join('')}</ul>`
      : '';
    banners += `
      <div class="banner info">
        <div>
          <strong>${deployed ? 'New commits on origin/main ready to deploy.' : 'No deploy recorded yet from this dashboard.'}</strong>
          ${pendingHtml}
        </div>
      </div>`;
  } else if (upToDate) {
    banners += `
      <div class="banner success">
        <div><strong>Up to date!</strong> Deployed commit <code class="mono">${esc(deployed?.shortSha)}</code> matches <code class="mono">origin/main</code>.</div>
      </div>`;
  }

  const deployLogSection =
    deployLines.length || deployRunning
      ? `
      <div class="card">
        <div class="card-header">
          <h2 class="card-title">Deploy Output</h2>
          <span class="badge ${deployRunning ? 'info' : deploySuccess ? 'ok' : 'err'}">
            ${deployRunning ? `Running: ${esc(deployPhase)}` : deploySuccess ? 'Succeeded' : 'Failed'}
          </span>
        </div>
        <div id="deploy-log" class="log-box">${esc(deployLines.join('\n') || 'Starting...')}</div>
      </div>`
      : '';

  const openPrsHtml = !s.ghAuth.authenticated
    ? `<div class="empty">Sign in with <code class="mono">gh auth login</code> to load Pull Requests.</div>`
    : s.prError
    ? `<div class="banner error">${esc(s.prError)}</div>`
    : s.openPrs.length === 0
    ? `<div class="empty">No open pull requests.</div>`
    : `<div class="list">${s.openPrs
        .map((pr) => {
          const wt = worktreeByBranch.get(pr.headRefName);
          const chk = checkSummary(pr);
          const method = mergeMethods[pr.number] ?? 'squash';
          return `
            <div class="row">
              <div class="row-main">
                <div class="row-title">
                  <span class="mono">#${pr.number}</span>
                  <a href="#" data-open-url="${esc(pr.url)}">${esc(pr.title)}</a>
                  ${pr.isDraft ? `<span class="badge">Draft</span>` : ''}
                  ${chk ? `<span class="badge ${chk.cls}">${esc(chk.label)}</span>` : ''}
                  ${wt ? `<span class="badge info">Worktree: ${esc(wt.name)}</span>` : ''}
                </div>
                <div class="row-meta">
                  <span class="mono">${esc(pr.headRefName)} → ${esc(pr.baseRefName)}</span>
                  <span class="diff-add">+${pr.additions ?? 0}</span>
                  <span class="diff-del">-${pr.deletions ?? 0}</span>
                  <span>${pr.changedFiles ?? 0} file(s)</span>
                  <span>updated ${relTime(pr.updatedAt)}</span>
                </div>
              </div>
              <div class="row-actions">
                <select class="sm" data-merge-method="${pr.number}" ${busy || deployRunning ? 'disabled' : ''}>
                  <option value="squash" ${method === 'squash' ? 'selected' : ''}>Squash</option>
                  <option value="merge" ${method === 'merge' ? 'selected' : ''}>Merge</option>
                  <option value="rebase" ${method === 'rebase' ? 'selected' : ''}>Rebase</option>
                </select>
                <button class="sm" data-merge-pr="${pr.number}" ${busy || deployRunning || pr.isDraft ? 'disabled' : ''}>Merge</button>
                <button class="sm success" data-merge-deploy-pr="${pr.number}" ${busy || deployRunning || pr.isDraft ? 'disabled' : ''}>Merge &amp; Deploy</button>
                ${
                  pendingClosePr === pr.number
                    ? `<button class="sm danger" data-confirm-close-pr="${pr.number}" ${busy || deployRunning ? 'disabled' : ''}>Confirm close</button>
                       <button class="sm" data-cancel-confirm="1">Cancel</button>`
                    : `<button class="sm danger" data-close-pr="${pr.number}" ${busy || deployRunning ? 'disabled' : ''}>Close</button>`
                }
              </div>
            </div>`;
        })
        .join('')}</div>`;

  const worktreesHtml = s.worktrees.length
    ? `<div class="list">${s.worktrees
        .map((wt, idx) => {
          const confirming = pendingRemoveWt === wt.path;
          const needsForce = wt.isDirty || !wt.mergedIntoMain;
          return `
          <div class="row">
            <div class="row-main">
              <div class="row-title">
                <span class="mono">${esc(wt.branch)}</span>
                ${wt.isMain ? `<span class="badge info">primary</span>` : ''}
                ${wt.isDirty ? `<span class="badge warn">dirty</span>` : `<span class="badge ok">clean</span>`}
                ${!wt.isMain && wt.mergedIntoMain ? `<span class="badge ok">merged</span>` : ''}
                ${wt.openPrNumber ? `<span class="badge info">PR #${wt.openPrNumber}</span>` : ''}
                ${!wt.openPrNumber && wt.mergedPrNumber ? `<span class="badge ok">Merged PR #${wt.mergedPrNumber}</span>` : ''}
              </div>
              <div class="row-meta">
                <span class="mono">${esc(wt.head)}</span>
                <span class="mono">${esc(wt.path)}</span>
              </div>
            </div>
            <div class="row-actions">
              ${
                wt.isMain
                  ? ''
                  : confirming
                  ? `<button class="sm danger" data-confirm-remove-wt="${idx}" ${busy || deployRunning ? 'disabled' : ''}>${needsForce ? 'Force remove & delete branch' : 'Confirm remove'}</button>
                     <button class="sm" data-cancel-confirm="1">Cancel</button>`
                  : `<button class="sm ${needsForce ? 'danger' : ''}" data-remove-wt="${idx}" ${busy || deployRunning ? 'disabled' : ''}>Remove worktree</button>`
              }
            </div>
          </div>`;
        })
        .join('')}</div>`
    : `<div class="empty">No worktrees found.</div>`;

  const mergedPrsHtml =
    s.mergedPrs.length > 0
      ? `<div class="list">${s.mergedPrs
          .slice(0, 6)
          .map(
            (pr) => `
            <div class="row">
              <div class="row-main">
                <div class="row-title">
                  <span class="mono">#${pr.number}</span>
                  <a href="#" data-open-url="${esc(pr.url)}">${esc(pr.title)}</a>
                </div>
                <div class="row-meta">
                  <span class="mono">${esc(pr.headRefName)}</span>
                  <span>merged ${relTime(pr.mergedAt)}</span>
                </div>
              </div>
            </div>`
          )
          .join('')}</div>`
      : `<div class="empty">No recently merged PRs.</div>`;

  root.innerHTML = `
    <div class="topbar">
      <h1>NetDaemon Deploy</h1>
      <div class="topbar-actions">
        <button id="btn-refresh" ${busy ? 'disabled' : ''}>${busy ? 'Refreshing...' : 'Fetch & Refresh'}</button>
      </div>
    </div>

    ${banners}

    <div class="card">
      <div class="card-header">
        <h2 class="card-title">Deploy Status</h2>
        ${deployed ? `<span class="badge">Last deploy ${relTime(deployed.timestamp)}</span>` : ''}
      </div>
      <div class="status-grid">
        <div class="stat-box">
          <div class="stat-label">Deployed</div>
          <div class="stat-sha">
            ${deployed ? esc(deployed.shortSha) : 'Not recorded'}
            ${deployed ? `<span class="badge ${upToDate ? 'ok' : 'warn'}">${upToDate ? 'current' : 'behind'}</span>` : ''}
          </div>
          <div class="stat-sub">${esc(deployed?.subject ?? 'Run a deploy to track')}</div>
        </div>
        <div class="stat-box">
          <div class="stat-label">Local main</div>
          <div class="stat-sha">
            ${esc(s.localMain?.shortSha ?? 'unknown')}
            ${s.commitsBehindOrigin.length ? `<span class="badge warn">${s.commitsBehindOrigin.length} behind</span>` : `<span class="badge ok">synced</span>`}
          </div>
          <div class="stat-sub">${esc(s.localMain?.subject ?? '')}</div>
        </div>
        <div class="stat-box">
          <div class="stat-label">origin/main</div>
          <div class="stat-sha">${esc(s.originMain?.shortSha ?? 'unknown')}</div>
          <div class="stat-sub">${esc(s.originMain?.subject ?? '')}</div>
        </div>
      </div>

      <div class="deploy-bar">
        <div class="checkboxes">
          <label><input type="checkbox" id="chk-tests" ${runTests ? 'checked' : ''} ${deployRunning ? 'disabled' : ''} /> Run dotnet test</label>
          <label><input type="checkbox" id="chk-codegen" ${runCodegen ? 'checked' : ''} ${deployRunning ? 'disabled' : ''} /> Run ./codegen.sh</label>
        </div>
        <button id="btn-deploy" class="primary" ${deployRunning || busy ? 'disabled' : ''}>
          ${deployRunning ? 'Deploying...' : 'Pull & Deploy main'}
        </button>
      </div>
    </div>

    ${deployLogSection}

    <div class="card">
      <div class="card-header">
        <h2 class="card-title">Open Pull Requests (${s.openPrs.length})</h2>
      </div>
      ${openPrsHtml}
    </div>

    <div class="card">
      <div class="card-header">
        <h2 class="card-title">Worktrees (${s.worktrees.length})</h2>
      </div>
      ${worktreesHtml}
    </div>

    ${
      s.ghAuth.authenticated
        ? `<div class="card">
             <div class="card-header"><h2 class="card-title">Recently Merged to main</h2></div>
             ${mergedPrsHtml}
           </div>`
        : ''
    }

    ${toastMsg ? `<div class="toast ${toastMsg.kind}">${esc(toastMsg.text)}</div>` : ''}
  `;

  // Wire events
  document.getElementById('btn-refresh')?.addEventListener('click', () => refresh(true));
  document.getElementById('btn-deploy')?.addEventListener('click', () => triggerDeploy());
  document.getElementById('chk-tests')?.addEventListener('change', (e) => {
    runTests = (e.target as HTMLInputElement).checked;
  });
  document.getElementById('chk-codegen')?.addEventListener('change', (e) => {
    runCodegen = (e.target as HTMLInputElement).checked;
  });

  root.querySelectorAll<HTMLAnchorElement>('[data-open-url]').forEach((a) => {
    a.addEventListener('click', (e) => {
      e.preventDefault();
      const url = a.dataset.openUrl;
      if (url) openExternal(url);
    });
  });

  root.querySelectorAll<HTMLSelectElement>('[data-merge-method]').forEach((sel) => {
    sel.addEventListener('change', () => {
      const n = Number(sel.dataset.mergeMethod);
      mergeMethods[n] = sel.value as 'squash' | 'merge' | 'rebase';
    });
  });

  root.querySelectorAll<HTMLButtonElement>('[data-merge-pr]').forEach((btn) => {
    btn.addEventListener('click', () => handleMergePr(Number(btn.dataset.mergePr)));
  });

  root.querySelectorAll<HTMLButtonElement>('[data-merge-deploy-pr]').forEach((btn) => {
    btn.addEventListener('click', () => triggerDeploy(Number(btn.dataset.mergeDeployPr)));
  });

  root.querySelectorAll<HTMLButtonElement>('[data-close-pr]').forEach((btn) => {
    btn.addEventListener('click', () => {
      pendingClosePr = Number(btn.dataset.closePr);
      pendingRemoveWt = null;
      render();
    });
  });

  root.querySelectorAll<HTMLButtonElement>('[data-confirm-close-pr]').forEach((btn) => {
    btn.addEventListener('click', () => handleClosePr(Number(btn.dataset.confirmClosePr)));
  });

  root.querySelectorAll<HTMLButtonElement>('[data-remove-wt]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.dataset.removeWt);
      const wt = s.worktrees[idx];
      if (wt) {
        pendingRemoveWt = wt.path;
        pendingClosePr = null;
        render();
      }
    });
  });

  root.querySelectorAll<HTMLButtonElement>('[data-confirm-remove-wt]').forEach((btn) => {
    btn.addEventListener('click', () => {
      const idx = Number(btn.dataset.confirmRemoveWt);
      const wt = s.worktrees[idx];
      if (wt) handleRemoveWorktree(wt);
    });
  });

  root.querySelectorAll<HTMLButtonElement>('[data-cancel-confirm]').forEach((btn) => {
    btn.addEventListener('click', () => {
      pendingClosePr = null;
      pendingRemoveWt = null;
      render();
    });
  });
}

let receivedToolInput = false;

function showFatalError(msg: string) {
  const root = document.getElementById('app');
  if (!root) return;
  root.innerHTML = `
    <div class="banner error">
      <div><strong>Dashboard error:</strong> ${esc(msg)}</div>
      <button id="btn-retry" class="sm">Retry</button>
    </div>`;
  document.getElementById('btn-retry')?.addEventListener('click', () => refresh(true));
}

async function boot() {
  try {
    if (isStandalone) {
      await refresh(true);
      return;
    }

    mcpApp!.ontoolinput = () => {
      receivedToolInput = true;
      const root = document.getElementById('app');
      if (root && !currentStatus) {
        root.innerHTML = `<div class="empty">Fetching Git status, Pull Requests, and worktrees...</div>`;
      }
    };

    mcpApp!.ontoolresult = (result) => {
      if (result?.structuredContent && typeof result.structuredContent === 'object') {
        applyStatus(result.structuredContent as unknown as StatusPayload);
        render();
      }
    };

    mcpApp!.onhostcontextchanged = (ctx) => {
      if (ctx.theme) applyDocumentTheme(ctx.theme);
      if (ctx.styles?.variables) applyHostStyleVariables(ctx.styles.variables);
    };

    await mcpApp!.connect();
    const ctx = mcpApp!.getHostContext();
    if (ctx?.theme) applyDocumentTheme(ctx.theme);
    if (ctx?.styles?.variables) applyHostStyleVariables(ctx.styles.variables);

    // Wait briefly for the host's initial open_deploy_dashboard result before
    // firing a fallback fetch (avoids duplicate fetches on launch).
    window.setTimeout(() => {
      if (!currentStatus && !receivedToolInput) {
        refresh(true);
      }
    }, 600);
  } catch (e) {
    showFatalError(String(e));
  }
}

boot();
