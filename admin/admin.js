'use strict';

// 管理令牌只放 sessionStorage：关闭标签即失效；不写 localStorage，也不进 URL。
const TOKEN_KEY = 'ft-admin-token';
const SVG_NS = 'http://www.w3.org/2000/svg';
const state = {
  token: sessionStorage.getItem(TOKEN_KEY) || '',
  usageDays: 7,
  accounts: { offset: 0, limit: 20, total: 0, query: '', plan: '' },
  codes: { status: 'open', offset: 0, limit: 20, total: 0 },
};

const $ = (id) => document.getElementById(id);

function setHidden(element, hidden) {
  element.classList.toggle('hidden', hidden);
}

function toast(message, isError = false) {
  const el = $('toast');
  el.textContent = message;
  el.classList.toggle('error', isError);
  el.classList.remove('hidden');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => el.classList.add('hidden'), 3200);
}

function showLogin(message = '') {
  state.token = '';
  sessionStorage.removeItem(TOKEN_KEY);
  setHidden($('app'), true);
  setHidden($('login'), false);
  $('login-error').textContent = message;
  $('token-input').value = '';
  $('token-input').focus();
}

function showApp() {
  setHidden($('login'), true);
  setHidden($('app'), false);
  $('token-state').textContent = '令牌仅保存在本标签页';
}

function refresh(task) {
  task().catch((error) => {
    if (error.message !== 'admin_forbidden') toast(error.message, true);
  });
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  headers.set('X-Admin-Token', state.token);
  if (options.body && !headers.has('Content-Type')) headers.set('Content-Type', 'application/json');
  const response = await fetch(path, { ...options, headers });
  // 403 是管理面统一关闭或令牌错误；回到登录页，避免每个调用点各写一遍。
  if (response.status === 403) {
    showLogin('令牌无效或后台未开启');
    throw new Error('admin_forbidden');
  }
  const data = await response.json().catch(() => null);
  if (!response.ok) {
    throw new Error((data && data.error) || `HTTP ${response.status}`);
  }
  return data;
}

function create(tag, className, text) {
  const el = document.createElement(tag);
  if (className) el.className = className;
  if (text !== undefined) el.textContent = text;
  return el;
}

function createSvg(tag) {
  return document.createElementNS(SVG_NS, tag);
}

function formatNumber(value) {
  return Number(value || 0).toLocaleString('zh-CN');
}

function formatYuan(micros) {
  return `${(Number(micros || 0) / 1_000_000).toFixed(2)} 元`;
}

function formatTime(unix) {
  if (!unix) return '—';
  return new Date(unix * 1000).toLocaleString('zh-CN', { hour12: false });
}

function formatBytes(bytes) {
  const value = Number(bytes || 0);
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / 1024 / 1024).toFixed(2)} MB`;
}

function formatDuration(seconds) {
  const value = Number(seconds || 0);
  const days = Math.floor(value / 86400);
  const hours = Math.floor((value % 86400) / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  if (days > 0) return `${days} 天 ${hours} 小时`;
  if (hours > 0) return `${hours} 小时 ${minutes} 分`;
  return `${minutes} 分`;
}

function planName(plan) {
  return { free: '免费', light: '轻享', standard: '标准' }[plan] || plan || '—';
}

function card(label, value, hint) {
  const el = create('div', 'card');
  el.append(create('div', 'label', label), create('div', 'value', value), create('div', 'hint', hint || ''));
  return el;
}

function summary(label, value) {
  const el = create('div');
  el.append(create('div', 'label', label), create('div', 'value', value));
  return el;
}

async function loadMetrics() {
  const data = await api('/api/v1/admin/metrics');
  $('cards').replaceChildren(
    card('账号总数', formatNumber(data.totalUsers), `30 天活跃 ${formatNumber(data.activeUsersLast30Days)}`),
    card('免费', formatNumber(data.freeUsers)),
    card('轻享', formatNumber(data.lightUsers)),
    card('标准', formatNumber(data.standardUsers)),
  );
  $('overview-updated').textContent = `更新于 ${new Date().toLocaleTimeString('zh-CN', { hour12: false })}`;
}

function renderUsageChart(byDay) {
  // 用 SVG 而不是给 DOM 设 style.height：Caddy 的 CSP 是 style-src 'self'，
  // 内联样式会被拦；SVG 的 presentation attribute 不受这条限制。
  const width = 600;
  const height = 120;
  const plot = 100;
  const gap = 4;
  const svg = createSvg('svg');
  svg.setAttribute('viewBox', `0 0 ${width} ${height}`);
  svg.setAttribute('role', 'img');
  svg.setAttribute('aria-label', '按日用量柱状图');
  const max = Math.max(1, ...byDay.map((row) => Number(row.chars || 0)));
  const slot = width / Math.max(1, byDay.length);
  byDay.forEach((row, index) => {
    const chars = Number(row.chars || 0);
    // 0 用量不画 1px 柱：30 天窗口里会变成一条误导的虚线。
    if (chars > 0) {
      const barHeight = Math.max(2, Math.round((chars / max) * plot));
      const rect = createSvg('rect');
      rect.setAttribute('x', String(Math.round(index * slot + 2)));
      rect.setAttribute('y', String(plot - barHeight));
      rect.setAttribute('width', String(Math.max(2, Math.round(slot - gap))));
      rect.setAttribute('height', String(barHeight));
      rect.setAttribute('class', 'bar');
      const title = createSvg('title');
      title.textContent = `${row.day}：${formatNumber(row.chars)} 字符 / ${formatNumber(row.requests)} 次`;
      rect.append(title);
      svg.append(rect);
    }
    if (byDay.length <= 7 || index % 5 === 0 || index === byDay.length - 1) {
      const text = createSvg('text');
      text.setAttribute('x', String(Math.round(index * slot + slot / 2)));
      text.setAttribute('y', '116');
      text.setAttribute('text-anchor', 'middle');
      text.setAttribute('class', 'day');
      text.textContent = String(row.day).slice(5);
      svg.append(text);
    }
  });
  $('usage-chart').replaceChildren(svg);
}

async function loadUsage() {
  const data = await api(`/api/v1/admin/usage?days=${state.usageDays}`);
  $('usage-summary').replaceChildren(
    summary('请求', formatNumber(data.total.requests)),
    summary('字符', formatNumber(data.total.chars)),
    summary('成本', formatYuan(data.total.costMicros)),
    summary('今日', `${formatYuan(data.today.costMicros)} / ${formatYuan(data.today.budgetMicros)}`),
  );
  renderUsageChart(data.byDay);

  const head = create('tr');
  ['来源', '请求', '字符', '成本'].forEach((text) => head.append(create('th', '', text)));
  const thead = create('thead');
  thead.append(head);
  const tbody = create('tbody');
  if (data.bySource.length === 0) {
    const row = create('tr');
    const cell = create('td', 'muted', '窗口内没有用量');
    cell.colSpan = 4;
    row.append(cell);
    tbody.append(row);
  } else {
    data.bySource.forEach((item) => {
      const row = create('tr');
      row.append(
        create('td', '', item.source),
        create('td', '', formatNumber(item.requests)),
        create('td', '', formatNumber(item.chars)),
        create('td', '', formatYuan(item.costMicros)),
      );
      tbody.append(row);
    });
  }
  const table = create('table');
  table.append(thead, tbody);
  $('usage-sources').replaceChildren(table);
}

async function loadAccounts() {
  const params = new URLSearchParams({
    limit: String(state.accounts.limit),
    offset: String(state.accounts.offset),
  });
  if (state.accounts.query) params.set('query', state.accounts.query);
  if (state.accounts.plan) params.set('plan', state.accounts.plan);
  const data = await api(`/api/v1/admin/accounts?${params.toString()}`);

  const head = create('tr');
  ['邮箱', '套餐', '到期', '最后活跃', '本月字符', '状态', '操作'].forEach((text) =>
    head.append(create('th', '', text)));
  const thead = create('thead');
  thead.append(head);
  const tbody = create('tbody');
  if (data.items.length === 0) {
    const row = create('tr');
    const cell = create('td', 'muted', '没有匹配的账号');
    cell.colSpan = 7;
    row.append(cell);
    tbody.append(row);
  } else {
    data.items.forEach((item) => {
      const row = create('tr');
      const planText = item.effectivePlan && item.effectivePlan !== item.plan
        ? `${planName(item.plan)}（现 ${planName(item.effectivePlan)}）`
        : planName(item.plan);
      row.append(create('td', '', item.email));
      row.append(create('td', '', planText));
      row.append(create('td', '', formatTime(item.planExpiresAt)));
      row.append(create('td', '', formatTime(item.lastSeenAt)));
      row.append(create('td', '', formatNumber(item.monthlyChars)));
      const statusCell = create('td');
      statusCell.append(create('span', item.disabled ? 'badge disabled' : 'badge ok', item.disabled ? '已禁用' : '正常'));
      row.append(statusCell);

      const actions = create('div', 'row-actions');
      const select = create('select');
      ['free', 'light', 'standard'].forEach((plan) => {
        const option = create('option', '', planName(plan));
        option.value = plan;
        option.selected = item.plan === plan;
        select.append(option);
      });
      const days = create('input');
      days.type = 'number';
      days.min = '0';
      days.max = '3650';
      days.value = '30';
      days.required = true;
      days.title = '必填；0 = 永久，正数 = 相对天数';
      days.setAttribute('aria-label', '开通天数');
      const apply = create('button', 'ghost', '改套餐');
      apply.type = 'button';
      apply.addEventListener('click', () => changePlan(item.email, select.value, days.value, apply));
      const toggle = create('button', item.disabled ? 'ghost' : 'danger', item.disabled ? '解禁' : '禁用');
      toggle.type = 'button';
      toggle.addEventListener('click', () => toggleAccount(item.email, !item.disabled, toggle));
      actions.append(select, days, apply);
      // 只给生效中的轻享账号显示：升级不重新发额度，也不顺延天数。
      if (!item.disabled && item.effectivePlan === 'light') {
        const upgrade = create('button', 'primary', '升级标准');
        upgrade.type = 'button';
        upgrade.title = '补差价升级，保留到期时间和本月已用字符';
        upgrade.addEventListener('click', () => upgradeAccount(item.email, upgrade));
        actions.append(upgrade);
      }
      actions.append(toggle);
      const actionsCell = create('td');
      actionsCell.append(actions);
      row.append(actionsCell);
      tbody.append(row);
    });
  }

  const table = create('table');
  table.append(thead, tbody);
  $('accounts').replaceChildren(table);

  const page = Math.floor(state.accounts.offset / state.accounts.limit) + 1;
  const pages = Math.max(1, Math.ceil(data.total / state.accounts.limit));
  $('accounts-page').textContent = `第 ${page} / ${pages} 页 · 共 ${formatNumber(data.total)} 个`;
  $('accounts-prev').disabled = state.accounts.offset === 0;
  $('accounts-next').disabled = state.accounts.offset + state.accounts.limit >= data.total;
}

async function changePlan(email, plan, rawDays, button) {
  // 普通改套餐不发送 days=null；升级按钮是例外，见 upgradeAccount。
  const text = String(rawDays ?? '').trim();
  const isFree = plan === 'free';
  if (!isFree && text === '') {
    toast('请填写天数：0 = 永久，正数 = 相对天数', true);
    return;
  }
  const days = isFree ? 0 : Number(text);
  if (!isFree && (!Number.isInteger(days) || days < 0 || days > 3650)) {
    toast('天数必须是 0–3650 的整数', true);
    return;
  }
  const duration = isFree ? '清除到期时间' : days === 0 ? '永久' : `${days} 天`;
  if (!confirm(`把 ${email} 改为 ${planName(plan)}（${duration}）？`)) return;
  button.disabled = true;
  try {
    // 邮箱里的 @ 走 URL 编码，避免被当成路径分段。
    await api(`/api/v1/admin/accounts/${encodeURIComponent(email)}/plan`, {
      method: 'POST',
      body: JSON.stringify({ plan, days }),
    });
    toast(`${email} 已改为 ${planName(plan)}`);
    await Promise.all([loadAccounts(), loadMetrics()]);
  } catch (error) {
    toast(error.message, true);
  } finally {
    button.disabled = false;
  }
}

async function upgradeAccount(email, button) {
  // days=null 让服务端保留原到期时间；用量表不动，所以本月已用字符继续计入标准额度。
  if (!confirm(`确认已收到差价，把 ${email} 升级到标准版？到期日不变，本月已用字符继续计入，不重置额度。`)) return;
  button.disabled = true;
  try {
    await api(`/api/v1/admin/accounts/${encodeURIComponent(email)}/plan`, {
      method: 'POST',
      body: JSON.stringify({ plan: 'standard', days: null }),
    });
    toast(`${email} 已升级标准，到期时间不变`);
    await Promise.all([loadAccounts(), loadMetrics()]);
  } catch (error) {
    toast(error.message, true);
  } finally {
    button.disabled = false;
  }
}

async function toggleAccount(email, disable, button) {
  if (disable && !confirm(`禁用 ${email}？现有登录会立即失效。`)) return;
  button.disabled = true;
  try {
    await api(`/api/v1/admin/accounts/${encodeURIComponent(email)}/${disable ? 'disable' : 'enable'}`, {
      method: 'POST',
    });
    toast(`${email} 已${disable ? '禁用' : '解禁'}`);
    await Promise.all([loadAccounts(), loadMetrics()]);
  } catch (error) {
    toast(error.message, true);
  } finally {
    button.disabled = false;
  }
}

async function loadCodes() {
  const params = new URLSearchParams({
    status: state.codes.status,
    limit: String(state.codes.limit),
    offset: String(state.codes.offset),
  });
  const data = await api(`/api/v1/admin/codes?${params.toString()}`);
  const head = create('tr');
  ['码', '套餐', '天数', '备注', '创建时间', '兑换人', '状态', '操作'].forEach((text) =>
    head.append(create('th', '', text)));
  const thead = create('thead');
  thead.append(head);
  const tbody = create('tbody');
  if (data.items.length === 0) {
    const row = create('tr');
    const cell = create('td', 'muted', '没有匹配的激活码');
    cell.colSpan = 8;
    row.append(cell);
    tbody.append(row);
  } else {
    data.items.forEach((item) => {
      const statusText = item.revokedAt ? '已作废' : item.redeemedAt ? '已兑换' : '未兑换';
      const statusClass = item.revokedAt ? 'badge disabled' : item.redeemedAt ? 'badge' : 'badge ok';
      const row = create('tr');
      row.append(create('td', '', item.code));
      row.append(create('td', '', planName(item.plan)));
      row.append(create('td', '', formatNumber(item.days)));
      row.append(create('td', '', item.note || '—'));
      row.append(create('td', '', formatTime(item.createdAt)));
      row.append(create('td', '', item.redeemedEmail || '—'));
      const statusCell = create('td');
      statusCell.append(create('span', statusClass, statusText));
      row.append(statusCell);
      const actionCell = create('td');
      if (!item.redeemedAt && !item.revokedAt) {
        const revoke = create('button', 'danger', '作废');
        revoke.type = 'button';
        revoke.addEventListener('click', () => revokeCode(item.code, revoke));
        actionCell.append(revoke);
      } else {
        actionCell.append(create('span', 'muted', '—'));
      }
      row.append(actionCell);
      tbody.append(row);
    });
  }
  const table = create('table');
  table.append(thead, tbody);
  $('codes').replaceChildren(table);

  const page = Math.floor(state.codes.offset / state.codes.limit) + 1;
  const pages = Math.max(1, Math.ceil(data.total / state.codes.limit));
  $('codes-page').textContent = `第 ${page} / ${pages} 页 · 共 ${formatNumber(data.total)} 张`;
  $('codes-prev').disabled = state.codes.offset === 0;
  $('codes-next').disabled = state.codes.offset + state.codes.limit >= data.total;
}

async function createCodes(event) {
  event.preventDefault();
  const button = event.currentTarget.querySelector('button[type="submit"]');
  button.disabled = true;
  try {
    const note = $('code-note').value.trim();
    const data = await api('/api/v1/admin/codes', {
      method: 'POST',
      body: JSON.stringify({
        plan: $('code-plan').value,
        days: Number($('code-days').value),
        count: Number($('code-count').value),
        note: note || null,
      }),
    });
    showCodeResult(data);
    await loadCodes();
  } catch (error) {
    toast(error.message, true);
  } finally {
    button.disabled = false;
  }
}

function showCodeResult(data) {
  const box = $('code-result');
  const head = create('div', 'section-head');
  head.append(create('strong', '', `已生成 ${data.codes.length} 张 ${planName(data.plan)} ${data.days} 天`));
  const copy = create('button', 'ghost', '复制全部');
  copy.type = 'button';
  copy.addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(data.codes.join('\n'));
      toast('已复制');
    } catch {
      toast('复制失败，请手动选择', true);
    }
  });
  head.append(copy);
  box.replaceChildren(
    head,
    create('pre', '', data.codes.join('\n')),
    create('div', 'muted', '这批码已写入账本；把上方状态切到「未兑换」可在列表中找回。'),
  );
  box.classList.remove('hidden');
}

async function revokeCode(code, button) {
  if (!confirm(`作废 ${code}？作废后不能兑换。`)) return;
  button.disabled = true;
  try {
    await api(`/api/v1/admin/codes/${encodeURIComponent(code)}/revoke`, { method: 'POST' });
    toast('已作废');
    await loadCodes();
  } catch (error) {
    toast(error.message, true);
  } finally {
    button.disabled = false;
  }
}

async function loadDiagnostics() {
  const data = await api('/api/v1/admin/diagnostics');
  const reasons = Object.entries(data.gateway.byReason)
    .sort((a, b) => b[1] - a[1])
    .slice(0, 3)
    .map(([key, value]) => `${key} ${formatNumber(value)}`)
    .join(' · ') || '无';
  $('diagnostics').replaceChildren(
    card('运行时长', formatDuration(data.uptimeSeconds), `自 ${formatTime(data.startedAtUnix)}`),
    card('网关请求', formatNumber(data.gateway.requests), `失败 ${formatNumber(data.gateway.failures)}`),
    card('失败原因', reasons, data.gateway.lastFailureReason
      ? `最后 ${data.gateway.lastFailureReason} · ${formatTime(data.gateway.lastFailureAtUnix)}`
      : '还没有失败'),
    card('语音请求', formatNumber(data.speech.requests), `失败 ${formatNumber(data.speech.failures)}`),
    card('数据库', formatBytes(data.database.sizeBytes), `WAL ${formatBytes(data.database.walBytes)}`),
  );
  $('diag-updated').textContent = `更新于 ${new Date().toLocaleTimeString('zh-CN', { hour12: false })}`;
}

async function loadAll() {
  // 五块互不依赖，用 allSettled 让单块失败不拖垮整页。
  const results = await Promise.allSettled([
    loadMetrics(),
    loadUsage(),
    loadAccounts(),
    loadCodes(),
    loadDiagnostics(),
  ]);
  for (const result of results) {
    if (result.status === 'rejected' && result.reason && result.reason.message !== 'admin_forbidden') {
      toast(result.reason.message || '加载失败', true);
      break;
    }
  }
}

async function login(event) {
  event.preventDefault();
  const token = $('token-input').value.trim();
  if (!token) return;
  state.token = token;
  try {
    await api('/api/v1/admin/metrics');
  } catch (error) {
    if (error.message !== 'admin_forbidden') showLogin(`连接失败：${error.message}`);
    return;
  }
  sessionStorage.setItem(TOKEN_KEY, token);
  showApp();
  await loadAll();
}

function bind() {
  $('login-form').addEventListener('submit', login);
  $('logout').addEventListener('click', () => showLogin());
  $('refresh').addEventListener('click', () => refresh(loadAll));
  document.querySelectorAll('[data-days]').forEach((button) => {
    button.addEventListener('click', () => {
      state.usageDays = Number(button.dataset.days);
      document.querySelectorAll('[data-days]').forEach((item) =>
        item.classList.toggle('active', item === button));
      refresh(loadUsage);
    });
  });
  $('account-search').addEventListener('submit', (event) => {
    event.preventDefault();
    state.accounts.query = $('account-query').value.trim();
    state.accounts.plan = $('account-plan').value;
    state.accounts.offset = 0;
    refresh(loadAccounts);
  });
  $('accounts-prev').addEventListener('click', () => {
    state.accounts.offset = Math.max(0, state.accounts.offset - state.accounts.limit);
    refresh(loadAccounts);
  });
  $('accounts-next').addEventListener('click', () => {
    state.accounts.offset += state.accounts.limit;
    refresh(loadAccounts);
  });
  $('code-form').addEventListener('submit', createCodes);
  $('code-status').addEventListener('change', () => {
    state.codes.status = $('code-status').value;
    state.codes.offset = 0;
    refresh(loadCodes);
  });
  $('codes-prev').addEventListener('click', () => {
    state.codes.offset = Math.max(0, state.codes.offset - state.codes.limit);
    refresh(loadCodes);
  });
  $('codes-next').addEventListener('click', () => {
    state.codes.offset += state.codes.limit;
    refresh(loadCodes);
  });
  $('code-refresh').addEventListener('click', () => refresh(loadCodes));
}

bind();
if (state.token) {
  showApp();
  refresh(loadAll);
} else {
  showLogin();
}