const $ = (id) => document.getElementById(id);
const state = { traceId: null, selected: null };

const format = {
  percent: (value) => `${(Number(value || 0) * 100).toFixed(1)}%`,
  decimal: (value, digits = 3) => Number(value || 0).toFixed(digits),
  cost: (value) => Number(value || 0).toFixed(4),
  integer: (value) => new Intl.NumberFormat().format(Number(value || 0)),
  duration: (value) => Number(value || 0) >= 1000
    ? `${(Number(value) / 1000).toFixed(2)}s`
    : `${Math.round(Number(value || 0))}ms`,
  time: (value) => new Date(value).toLocaleString()
};

function requestHeaders() {
  const headers = new Headers({ accept: 'application/json' });
  const token = $('token').value.trim();
  if (token) headers.set('authorization', `Bearer ${token}`);
  const tenant = $('tenant').value.trim();
  const actor = $('actor').value.trim();
  if (tenant) headers.set('x-tenant-id', tenant);
  if (actor) headers.set('x-actor-id', actor);
  return headers;
}

async function readResponse(response) {
  if (response.ok) return await response.json();
  let detail = `HTTP ${response.status}`;
  try {
    const problem = await response.json();
    detail = problem.detail || problem.title || detail;
  } catch {}
  throw new Error(detail);
}

async function loadSummary() {
  setNotice();
  setConnection('loading', 'Connecting');
  $('status-message').textContent = 'Loading persisted observability data…';
  $('refresh').disabled = true;
  const end = new Date();
  const start = new Date(end.getTime() - Number($('window').value) * 60 * 60 * 1000);
  const url = new URL('/v1/observability/summary', window.location.origin);
  url.searchParams.set('from', start.toISOString());
  url.searchParams.set('to', end.toISOString());
  if ($('task').value.trim()) url.searchParams.set('taskType', $('task').value.trim());
  if ($('policy').value.trim()) url.searchParams.set('policyVersion', $('policy').value.trim());
  try {
    const data = await readResponse(await fetch(url, { headers: requestHeaders() }));
    renderSummary(data);
    setConnection('connected', 'connected');
    $('status-message').textContent = `API connected · tenant ${$('tenant').value.trim() || 'unknown'} · ${$('window').selectedOptions[0].text.toLowerCase()}`;
    $('last-updated').textContent = `Updated ${new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })}`;
  } catch (error) {
    setConnection('error', 'disconnected');
    $('status-message').textContent = 'The observability API could not be read.';
    $('empty-state').hidden = true;
    $('observations').hidden = true;
    throw error;
  } finally {
    $('refresh').disabled = false;
  }
}

function renderSummary(data) {
  const totals = data.totals;
  const hasExecutions = totals.executions > 0;
  const hasCompleted = totals.completed > 0;
  $('empty-state').hidden = hasExecutions;
  $('observations').hidden = !hasExecutions;
  $('empty-scope').textContent = `tenant “${$('tenant').value.trim() || 'unknown'}” in ${$('window').selectedOptions[0].text.toLowerCase()}`;
  $('completion').textContent = hasExecutions ? format.percent(totals.completionRate) : '—';
  $('execution-count').textContent = hasExecutions
    ? `${format.integer(totals.completed)} completed / ${format.integer(totals.executions)} observed`
    : 'No observations in this view';
  $('quality').textContent = hasCompleted ? format.decimal(totals.averageQuality) : '—';
  $('cost').textContent = hasCompleted ? format.cost(totals.cost) : '—';
  $('model-calls').textContent = hasCompleted
    ? `${format.integer(totals.modelCalls)} model · ${format.integer(totals.toolCalls)} tool calls`
    : 'Gateway-reported units';
  $('latency').textContent = hasCompleted ? format.duration(totals.averageLatencyMilliseconds) : '—';
  $('tokens').textContent = hasCompleted ? format.integer(totals.inputTokens + totals.outputTokens) : '—';
  $('token-split').textContent = hasCompleted
    ? `${format.integer(totals.inputTokens)} in · ${format.integer(totals.outputTokens)} out`
    : 'Input + output';
  $('no-model').textContent = hasCompleted ? format.percent(totals.noModelResolutions / totals.completed) : '—';
  $('avoided').textContent = hasCompleted
    ? `${format.integer(totals.modelCallsAvoided)} model calls avoided`
    : 'Model calls avoided';
  $('sample-badge').textContent = `${format.integer(data.sampledExecutions)} sampled${data.truncated ? ' · bounded' : ''}`;
  renderGroups(data.groups);
  renderMemory(data.memory);
  renderRecent(data.recentExecutions);
  renderGateways(data.gatewayGroups || []);
}

function renderGroups(groups) {
  const body = $('groups');
  body.replaceChildren();
  if (!groups.length) return body.append(emptyRow('No matching task and policy observations.', 6));
  for (const group of groups) {
    const row = document.createElement('tr');
    const task = document.createElement('td');
    task.className = 'task-cell';
    task.append(text('strong', group.taskType), text('small', group.policyVersion));
    row.append(
      task,
      cell(format.integer(group.metrics.executions)),
      cell(format.decimal(group.metrics.averageQuality), 'metric-good'),
      cell(format.cost(group.metrics.cost)),
      cell(format.duration(group.metrics.averageLatencyMilliseconds)),
      cell(format.integer(group.metrics.noModelResolutions))
    );
    body.append(row);
  }
}

function renderGateways(groups) {
  const body = $('gateway-groups');
  body.replaceChildren();
  if (!groups.length) return body.append(emptyRow('No provider route observations in this view.', 7));
  for (const group of groups) {
    const row = document.createElement('tr');
    const deployment = text('td', '', 'route-cell');
    deployment.append(text('strong', group.deploymentId), text('small', `${group.gatewayId} · ${group.modelVersion}`));
    const provider = text('td', '', 'route-cell');
    provider.append(text('strong', group.provider), text('small', group.region));
    const circuit = text('span', group.circuitState, `circuit-state ${String(group.circuitState).toLowerCase()}`);
    const circuitCell = document.createElement('td');
    circuitCell.append(circuit);
    const failureLabel = group.failureClass ? ` · ${group.failureClass}` : '';
    row.append(
      deployment,
      provider,
      circuitCell,
      cell(format.integer(group.metrics.attempts)),
      cell(`${format.integer(group.metrics.failures)}${failureLabel}`),
      cell(format.integer(group.metrics.fallbacks)),
      cell(format.duration(group.metrics.averageLatencyMilliseconds))
    );
    body.append(row);
  }
}

function renderMemory(memory) {
  $('hit-rate').textContent = memory.considered ? format.percent(memory.hitRate) : '—';
  $('memory-accepted').textContent = `${format.integer(memory.accepted)} accepted`;
  $('memory-rejected').textContent = `${format.integer(memory.rejected)} rejected`;
  const root = $('memory-bars');
  root.replaceChildren();
  if (!memory.resolvers.length) return root.append(text('p', 'No reuse candidates were considered.', 'empty'));
  for (const resolver of memory.resolvers) {
    const row = text('div', '', 'bar-row');
    const meta = text('div', '', 'bar-meta');
    meta.append(text('span', resolver.resolver), text('span', `${resolver.accepted}/${resolver.considered} hits`));
    const track = text('div', '', 'bar-track');
    const fill = text('div', '', 'bar-fill');
    fill.style.width = `${resolver.considered ? resolver.accepted / resolver.considered * 100 : 0}%`;
    track.append(fill);
    const reasons = Object.entries(resolver.reasons).map(([key, value]) => `${key} ${value}`).join(' · ');
    row.append(meta, track, text('p', reasons, 'reason-list'));
    root.append(row);
  }
}

function renderRecent(executions) {
  const root = $('recent');
  root.replaceChildren();
  if (!executions.length) return root.append(text('p', 'No matching executions.', 'empty'));
  for (const execution of executions) {
    const button = text('button', '', 'recent-row');
    button.type = 'button';
    button.dataset.executionId = execution.executionId;
    const dot = text('span', '', `status-dot ${execution.status.toLowerCase()}`);
    const main = text('span', '', 'recent-main');
    main.append(text('strong', execution.taskType), text('small', `${execution.policyVersion} · ${execution.executionId.slice(0, 12)}`));
    const stats = text('span', '', 'recent-stats');
    stats.append(text('strong', execution.resolutionLevel || execution.status), text('small', `${format.duration(execution.durationMilliseconds)} · ${format.cost(execution.cost)} cost`));
    button.append(dot, main, stats);
    button.addEventListener('click', () => loadTimeline(execution.executionId, button));
    root.append(button);
  }
}

async function loadTimeline(executionId, button) {
  document.querySelectorAll('.recent-row').forEach((row) => row.classList.toggle('active', row === button));
  const url = new URL(`/v1/observability/executions/${encodeURIComponent(executionId)}`, window.location.origin);
  try {
    const data = await readResponse(await fetch(url, { headers: requestHeaders() }));
    renderTimeline(data);
  } catch (error) {
    setNotice(error.message);
  }
}

function renderTimeline(data) {
  state.traceId = data.traceId;
  $('copy-trace').disabled = false;
  const summary = $('timeline-summary');
  summary.replaceChildren();
  const grid = text('div', '', 'trace-grid');
  grid.append(traceFact('Trace ID', data.traceId), traceFact('Policy', data.policyVersion), traceFact('Duration', format.duration(data.durationMilliseconds)));
  summary.append(grid);
  const root = $('timeline');
  root.replaceChildren();
  for (const event of data.events) {
    const item = document.createElement('li');
    const title = text('div', '', 'event-title');
    title.append(text('strong', event.type), text('time', `+${format.duration(event.offsetMilliseconds)}`));
    const payload = text('pre', JSON.stringify(event.data, null, 2), 'event-data');
    item.append(title, payload);
    root.append(item);
  }
}

function traceFact(label, value) {
  const item = document.createElement('div');
  item.append(text('span', label), text('strong', value));
  return item;
}

function text(tag, value, className) {
  const node = document.createElement(tag);
  node.textContent = value;
  if (className) node.className = className;
  return node;
}

function cell(value, className) {
  return text('td', value, className);
}

function emptyRow(message, columns) {
  const row = document.createElement('tr');
  const value = text('td', message, 'empty');
  value.colSpan = columns;
  row.append(value);
  return row;
}

function setNotice(message) {
  const notice = $('notice');
  notice.hidden = !message;
  notice.textContent = message || '';
}

function setConnection(status, label) {
  $('connection').dataset.state = status;
  $('connection-label').textContent = label;
}

$('query-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  try { await loadSummary(); } catch (error) { setNotice(error.message); }
});

$('copy-trace').addEventListener('click', async () => {
  if (!state.traceId) return;
  await navigator.clipboard.writeText(state.traceId);
  $('copy-trace').textContent = 'Copied';
  setTimeout(() => { $('copy-trace').textContent = 'Copy trace ID'; }, 1200);
});

loadSummary().catch((error) => setNotice(error.message));
