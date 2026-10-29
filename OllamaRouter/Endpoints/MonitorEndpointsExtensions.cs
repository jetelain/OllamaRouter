using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Lightweight, dependency-free monitoring UI: shows the multi-target fleet (per-target
/// liveness, active model, in-flight requests, hardware profile, and context allocation) plus
/// requests currently in progress and the recent request history with model, estimated and
/// actual tokens, and elapsed time. It reads in-memory state from
/// <see cref="IActivityMonitorService"/> and aggregated token statistics (persisted, current day
/// and last 7 days) from <see cref="IActivityStatisticsService"/>, so it has no impact on VRAM.
/// </summary>
public static class MonitorEndpointsExtensions
{
    public static WebApplication MapOllamaMonitorEndpoints(this WebApplication app)
    {
        app.MapGet("/monitor/api", async (
            IActivityMonitorService activityMonitor,
            IActivityStatisticsService activityStatistics,
            IOptions<OllamaRouterOptions> options,
            ITargetAvailabilityService targetAvailability,
            IModelCatalogCacheService modelCatalogCache,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
        {
            // Optional enrichment services: null-tolerant so minimal (test) apps that do not
            // register them keep working with the reduced (catalog-only) liveness view.
            var monitorResponse = await BuildMonitorStateResponseAsync(
                activityMonitor,
                activityStatistics,
                options,
                targetAvailability,
                modelCatalogCache,
                cancellationToken,
                includeOverhead: true,
                liveness: serviceProvider.GetService<ITargetLivenessService>(),
                executionTracker: serviceProvider.GetService<ITargetExecutionTracker>(),
                contextShrinkManager: serviceProvider.GetService<IContextAutoShrinkManager>());

            return Results.Json(monitorResponse, OllamaRouterJsonSerializerContext.Default.MonitorStateResponse);
        });

        app.MapPost("/targets", (TargetsUpdateRequest request, ITargetAvailabilityService targetAvailability) =>
        {
            var flags = request.AsFlags();
            targetAvailability.Update(flags);
            return Results.Ok(new TargetsUpdateResponse(flags));
        });

        app.MapPost("/monitor/reclaim-vram", async (
            [FromQuery] bool? disableTarget,
            IOllamaModelCatalogClient catalogClient,
            ITargetAvailabilityService targetAvailability,
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
            await ReclaimLocalLegacyAsync(
                disableTarget ?? true, catalogClient, targetAvailability, options,
                serviceProvider.GetService<IContextAutoShrinkManager>(), cancellationToken));

        app.MapPost("/api/models/unload", async (
            [FromQuery] bool? disableTarget,
            IOllamaModelCatalogClient catalogClient,
            ITargetAvailabilityService targetAvailability,
            IOptions<OllamaRouterOptions> options,
            IServiceProvider serviceProvider,
            CancellationToken cancellationToken) =>
            await ReclaimLocalLegacyAsync(
                disableTarget ?? false, catalogClient, targetAvailability, options,
                serviceProvider.GetService<IContextAutoShrinkManager>(), cancellationToken));

        app.MapGet("/monitor", () => Results.Content(MonitorPageHtml, "text/html"));

        return app;
    }

    /// <summary>
    /// Shared legacy per-"Local"-target reclaim core for <c>POST /monitor/reclaim-vram</c>
    /// (default <c>disableTarget: true</c>) and <c>POST /api/models/unload</c> (default
    /// <c>false</c>): keeps the exact legacy behavior — "Local" is disabled FIRST (even when
    /// <c>LocalUrl</c> is unconfigured, in which case the response is
    /// <c>success: false</c> + "Local URL is not configured.") and the unload is routed
    /// through the shared per-target <see cref="VramReclaimCore"/>, preserving the legacy
    /// <see cref="ReclaimVramResponse"/> shape byte-for-byte.
    /// </summary>
    private static async Task<IResult> ReclaimLocalLegacyAsync(
        bool shouldDisable,
        IOllamaModelCatalogClient catalogClient,
        ITargetAvailabilityService targetAvailability,
        IOptions<OllamaRouterOptions> options,
        IContextAutoShrinkManager? shrinkManager,
        CancellationToken cancellationToken)
    {
        if (shouldDisable)
        {
            DisableLocalTarget(targetAvailability);
        }

        var effectiveTargets = options.Value.GetEffectiveTargets();
        var localTarget = effectiveTargets.FirstOrDefault(t => string.Equals(t.Name, "Local", StringComparison.OrdinalIgnoreCase))
            ?? effectiveTargets.FirstOrDefault(t => OllamaProcessLauncher.IsLoopback(t.Url));

        var localUrl = localTarget?.Url?.TrimEnd('/');
#pragma warning disable CS0618
        if (string.IsNullOrWhiteSpace(localUrl))
        {
            localUrl = options.Value.LocalUrl?.TrimEnd('/');
        }
#pragma warning restore CS0618

        if (string.IsNullOrWhiteSpace(localUrl))
        {
            return Results.Ok(new ReclaimVramResponse(
                Success: false,
                Message: "Local URL is not configured.",
                Count: 0,
                UnloadedModels: Array.Empty<string>(),
                LocalDisabled: shouldDisable));
        }

        var result = await VramReclaimCore.ReclaimTargetAsync(
            localTarget ?? new OllamaTargetOptions { Name = "Local", Url = localUrl },
            false, // Already disabled above (legacy pre-disable ordering).
            catalogClient,
            targetAvailability,
            shrinkManager,
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(new ReclaimVramResponse(
            Success: true,
            Message: null,
            Count: result.UnloadedModels.Count,
            UnloadedModels: result.UnloadedModels,
            LocalDisabled: shouldDisable));
    }

    // "Reclaim" behavior: disable the target named "Local" (case-insensitive); when no such
    // target exists in the fleet the availability service ignores the unknown name (skip).
    private static void DisableLocalTarget(ITargetAvailabilityService targetAvailability)
    {
        targetAvailability.Update(
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Local"] = false
            });
    }

    private const string MonitorPageHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<title>OllamaRouter - Activity Monitor</title>
<style>
  body { font-family: Segoe UI, Arial, sans-serif; margin: 1.5rem; background: #1e1e1e; color: #ddd; }
  h1 { font-size: 1.3rem; }
  h2 { font-size: 1.05rem; color: #bbb; margin-top: 1.75rem; }
  .fleet-table { width: 100%; margin-bottom: 0.5rem; }
  .lv-badge { display: inline-flex; align-items: center; gap: 6px; font-size: 0.85rem; }
  .lv-badge .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; }
  .lv-online .dot { background: #3ba55c; }
  .lv-offline .dot { background: #e67e22; }
  .lv-probing .dot { background: #ffb300; }
  .lv-disabled .dot { background: #b33333; }
  .reclaim-row { display: flex; align-items: center; gap: 1rem; margin: 0.25rem 0 1.25rem; }
  .reclaim-row .btn-reclaim { width: auto; }
  .per-target-savings { font-size: 0.78rem; color: #9cdcfe; margin-top: 0.45rem; display: flex; flex-wrap: wrap; gap: 0.15rem 0.9rem; }
  .btn-reclaim { display: inline-block; padding: 0.4rem 0.6rem; font-size: 0.78rem; font-weight: 500; color: #eee; background: #333; border: 1px solid #555; border-radius: 6px; cursor: pointer; text-align: center; }
  .btn-reclaim:hover:not(:disabled) { background: #444; border-color: #888; color: #fff; }
  .btn-reclaim:disabled { opacity: 0.6; cursor: not-allowed; }
  .reclaim-status { font-size: 0.75rem; color: #9cdcfe; line-height: 1.2; word-break: break-word; }
  table { border-collapse: collapse; width: 100%; }
  th, td { text-align: left; padding: 0.35rem 0.75rem; border-bottom: 1px solid #333; font-size: 0.9rem; }
  th { color: #999; font-weight: 600; }
  tr.fail { color: #ff6b6b; }
  tr.pending { color: #ffd479; }
  tr.total td { color: #fff; font-weight: 600; border-top: 1px solid #555; }
  td.num { text-align: right; }
  .stats th { text-align: center; }
  .stats th:first-child, .stats td:first-child { text-align: left; }
  .stats .today { background: #262626; }
  .target-Local { color: #3f9eff; }
  .target-Remote { color: #34e0a1; }
  .target-Cloud { color: #c48bff; }
  .progress-seg-local { background-color: #3f9eff; background-image: repeating-linear-gradient(45deg, rgba(255,255,255,0.22) 0 6px, transparent 6px 12px); }
  .progress-seg-remote { background-color: #34e0a1; background-image: repeating-linear-gradient(45deg, rgba(255,255,255,0.22) 0 6px, transparent 6px 12px); }
  .progress-seg-cloud { background-color: #c48bff; background-image: repeating-linear-gradient(45deg, rgba(255,255,255,0.22) 0 6px, transparent 6px 12px); }
  .progress-seg-neutral { background-color: #7a8aa0; background-image: repeating-linear-gradient(45deg, rgba(255,255,255,0.22) 0 6px, transparent 6px 12px); }
  .empty { color: #777; font-style: italic; padding: 0.5rem 0.75rem; }
  .stats-summary-row { display: flex; gap: 1rem; margin-top: 1rem; align-items: stretch; }
  .usage-ratio-container { flex: 2; min-width: 0; background: #262626; border-radius: 8px; padding: 0.85rem 1.1rem; border: 1px solid #333; display: flex; flex-direction: column; justify-content: space-between; }
  .progress-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.6rem; font-size: 0.85rem; color: #aaa; }
  .progress-summary { font-size: 0.8rem; color: #888; }
  .progress-bar { display: flex; height: 18px; border-radius: 6px; overflow: hidden; background: #181818; box-shadow: inset 0 1px 3px rgba(0,0,0,0.5); }
  .progress-seg { height: 100%; position: relative; overflow: hidden; transition: width 0.3s ease; }
  .progress-seg + .progress-seg { border-left: 2px solid #181818; }
  .progress-empty { width: 100%; display: flex; align-items: center; justify-content: center; font-size: 0.78rem; color: #666; font-style: italic; }
  .progress-legend { display: flex; gap: 1.2rem; margin-top: 0.65rem; font-size: 0.82rem; flex-wrap: wrap; }
  .legend-item { display: flex; align-items: center; gap: 6px; }
  .legend-dot { width: 10px; height: 10px; border-radius: 2px; }
  .cost-container { flex: 1; display: flex; gap: 1rem; min-width: 0; }
  .cost-container.has-cloud { flex: 2; }
  .cost-card { flex: 1; min-width: 0; background: #262626; border-radius: 8px; padding: 0.85rem 1.1rem; border: 1px solid #333; display: flex; flex-direction: column; justify-content: space-between; }
  .cost-card.savings { border-left: 4px solid #34e0a1; }
  .cost-card.overflow { border-left: 4px solid #c48bff; }
  .cost-title { font-size: 0.82rem; color: #aaa; }
  .cost-value { font-size: 1.4rem; font-weight: 700; color: #fff; line-height: 1.2; margin: 0.2rem 0; }
  .cost-desc { font-size: 0.75rem; color: #888; }
</style>
</head>
<body>
<h1>OllamaRouter - Activity Monitor</h1>
<table class="fleet-table" id="fleetTable">
  <thead>
    <tr><th>Target</th><th>URL</th><th>HW</th><th>Liveness</th><th>Active Model</th><th>In-Flight</th><th>Context</th><th>Enabled</th></tr>
  </thead>
  <tbody id="fleetRows"></tbody>
</table>

<div class="reclaim-row">
  <button id="reclaimBtn" class="btn-reclaim" title="Unload all running models in local Ollama instance and disable local target (e.g. to play video games)">\uD83C\uDFAE Reclaim local VRAM</button>
  <div id="reclaimStatus" class="reclaim-status"></div>
</div>

<h2>Statistics</h2>
<table class="stats">
  <thead>
    <tr><th rowspan="2">Target</th><th colspan="3" class="today">Today</th><th colspan="3">Last 7 days</th></tr>
    <tr><th class="today">Req</th><th class="today">In</th><th class="today">Out</th><th>Req</th><th>In</th><th>Out</th></tr>
  </thead>
  <tbody id="statsRows"></tbody>
</table>

<div class="stats-summary-row" id="statsSummaryRow">
  <div class="usage-ratio-container" id="usageRatioContainer">
    <div class="progress-header">
      <span>Target usage ratio (last 7 days)</span>
      <span id="usageRatioSummary" class="progress-summary"></span>
    </div>
    <div class="progress-bar" id="usageProgressBar"></div>
    <div class="progress-legend" id="usageProgressLegend"></div>
  </div>

  <div class="cost-container" id="costContainer" style="display: none;">
    <div class="cost-card savings">
      <div class="cost-title">💰 Estimated savings (last 7 days)</div>
      <div class="cost-value" id="estimatedSavingsValue">-</div>
      <div class="cost-desc">Saved thanks to local &amp; remote routing</div>
      <div class="per-target-savings" id="perTargetSavingsList"></div>
    </div>
    <div class="cost-card overflow" id="cloudCostCard">
      <div class="cost-title">☁️ Cloud overflow cost (last 7 days)</div>
      <div class="cost-value" id="cloudCostValue">-</div>
      <div class="cost-desc">Cost incurred from cloud overflow</div>
    </div>
  </div>
</div>

<h2>In progress</h2>
<table>
  <thead>
    <tr><th>Target</th><th>Model</th><th>In (est.)</th><th>Running for</th></tr>
  </thead>
  <tbody id="inProgressRows"></tbody>
</table>

<h2>Recent requests</h2>
<table>
  <thead>
    <tr><th>Time</th><th>Target</th><th>Model</th><th>In (est. / act.)</th><th>Out</th><th>Elapsed</th><th>Status</th></tr>
  </thead>
  <tbody id="rows"></tbody>
</table>
<h2>Token estimation overhead recommendation</h2>
<div id="overheadContent"></div>
<script>
const targetIcons = { Local: '🖥️', Remote: '📡', Cloud: '☁️' };

// Case-insensitive lookup of the display icon for a target name (fleet names have no icon).
function targetIcon(name) {
  for (const key of Object.keys(targetIcons)) {
    if (name.toLowerCase() === key.toLowerCase()) {
      return targetIcons[key];
    }
  }
  return '';
}

// Case-insensitive lookup of the CSS color class for a target name (neutral fallback for fleet names).
function targetColorClass(name) {
  for (const key of ['Local', 'Remote', 'Cloud']) {
    if (name.toLowerCase() === key.toLowerCase()) {
      return key.toLowerCase();
    }
  }
  return 'neutral';
}

function targetLabel(name) {
  return (targetIcon(name) ? targetIcon(name) + ' ' : '') + name;
}

function td(text, className) {
  const cell = document.createElement('td');
  if (className) {
    cell.className = className;
  }
  cell.textContent = text;
  return cell;
}

function targetCell(target) {
  const cell = document.createElement('td');
  const span = document.createElement('span');
    span.className = 'target-' + target;
    span.textContent = targetLabel(target);
    cell.appendChild(span);
  return cell;
}

function addRow(tbody, cells, className) {
  const tr = document.createElement('tr');
  if (className) {
    tr.className = className;
  }
  for (const cell of cells) {
    tr.appendChild(cell);
  }
  tbody.appendChild(tr);
}

function clear(element) {
  while (element.firstChild) {
    element.removeChild(element.firstChild);
  }
}

function num(value) {
  if (value == null) {
    return '-';
  }
  return value >= 1000 ? value.toString().replace(/\B(?=(\d{3})+(?!\d))/g, ' ') : String(value);
}

function tokens(value) {
  if (value == null) {
    return '-';
  }
  if (value >= 1000000) {
    return (value / 1000000).toFixed(1) + 'M';
  }
  return value >= 1000 ? (value / 1000).toFixed(1) + 'k' : String(value);
}

function renderStatistics(tbodyId, today, last7Days) {
  const tbody = document.getElementById(tbodyId);
  clear(tbody);
  const names = Object.keys(today).filter(name => name !== 'total');
  for (const name of names) {
    addRow(tbody, [
      targetCell(name),
      td(num(today[name].requests), 'num today'),
      td(tokens(today[name].inputTokens), 'num today'),
      td(tokens(today[name].outputTokens), 'num today'),
      td(num(last7Days[name].requests), 'num'),
      td(tokens(last7Days[name].inputTokens), 'num'),
      td(tokens(last7Days[name].outputTokens), 'num')
    ]);
  }
  addRow(tbody, [
    td('Total'),
    td(num(today.total.requests), 'num today'),
    td(tokens(today.total.inputTokens), 'num today'),
    td(tokens(today.total.outputTokens), 'num today'),
    td(num(last7Days.total.requests), 'num'),
    td(tokens(last7Days.total.inputTokens), 'num'),
    td(tokens(last7Days.total.outputTokens), 'num')
  ], 'total');
}

function formatCost(amount, currency) {
  if (amount == null) {
    return '-';
  }
  const curr = currency || '$';
  if (amount === 0) {
    return curr + '0.00';
  }
  if (amount < 0.01) {
    return curr + amount.toFixed(4);
  }
  return curr + amount.toFixed(2);
}

function renderUsageRatio(last7Days) {
  const names = Object.keys(last7Days).filter(name => name !== 'total');
  const entries = names.map(name => {
    const stats = last7Days[name] || { inputTokens: 0, outputTokens: 0, requests: 0 };
    return { name, tokens: stats.inputTokens + stats.outputTokens, requests: stats.requests };
  });
  const totalTokens = entries.reduce((sum, e) => sum + e.tokens, 0);

  const bar = document.getElementById('usageProgressBar');
  const legend = document.getElementById('usageProgressLegend');
  const summary = document.getElementById('usageRatioSummary');
  clear(bar);
  clear(legend);

  if (totalTokens === 0) {
    const emptyDiv = document.createElement('div');
    emptyDiv.className = 'progress-empty';
    emptyDiv.textContent = 'No token activity in the last 7 days';
    bar.appendChild(emptyDiv);
    summary.textContent = '0 tokens';
    return;
  }

  summary.textContent = tokens(totalTokens) + ' tokens';

  function addSeg(className, pct, title) {
    if (pct <= 0) return;
    const seg = document.createElement('div');
    seg.className = 'progress-seg ' + className;
    seg.style.width = pct.toFixed(2) + '%';
    seg.title = title;
    bar.appendChild(seg);
  }

  for (const entry of entries) {
    const pct = entry.tokens / totalTokens * 100;
    addSeg('progress-seg-' + targetColorClass(entry.name), pct, entry.name + ': ' + pct.toFixed(1) + '% (' + tokens(entry.tokens) + ' tokens, ' + num(entry.requests) + ' req)');
  }

  for (const entry of entries) {
    const pct = entry.tokens / totalTokens * 100;
    const item = document.createElement('div');
    item.className = 'legend-item';
    const dot = document.createElement('span');
    dot.className = 'legend-dot progress-seg-' + targetColorClass(entry.name);
    item.appendChild(dot);
    const span = document.createElement('span');
    span.textContent = targetLabel(entry.name) + ': ' + pct.toFixed(1) + '%';
    item.appendChild(span);
    legend.appendChild(item);
  }
}

function renderCost(cost, cloudEnabled) {
  const container = document.getElementById('costContainer');
  if (!cost) {
    container.style.display = 'none';
    return;
  }
  container.style.display = 'flex';

  const savingsEl = document.getElementById('estimatedSavingsValue');
  const cloudCostEl = document.getElementById('cloudCostValue');
  const cloudCard = document.getElementById('cloudCostCard');
  const perTargetList = document.getElementById('perTargetSavingsList');

  savingsEl.textContent = formatCost(cost.estimatedSavings, cost.currency);
  cloudCostEl.textContent = formatCost(cost.cloudOverflowCost, cost.currency);

  clear(perTargetList);
  const perTarget = cost.perTargetSavings || {};
  for (const [name, amount] of Object.entries(perTarget)) {
    const span = document.createElement('span');
    span.textContent = targetLabel(name) + ': ~' + formatCost(amount, cost.currency);
    perTargetList.appendChild(span);
  }

  if (cloudEnabled) {
    container.classList.add('has-cloud');
    if (cloudCard) {
      cloudCard.style.display = 'flex';
    }
  } else {
    container.classList.remove('has-cloud');
    if (cloudCard) {
      cloudCard.style.display = 'none';
    }
  }
}

let isReclaiming = false;
let reclaimStatusText = '';
let statusResetTimer = null;

function renderReclaimButtonState() {
  const btn = document.getElementById('reclaimBtn');
  if (btn) {
    btn.disabled = isReclaiming;
    btn.textContent = isReclaiming ? '⏳ Reclaiming...' : '🎮 Reclaim local VRAM';
  }
  const statusDiv = document.getElementById('reclaimStatus');
  if (statusDiv) {
    statusDiv.textContent = reclaimStatusText;
  }
}

async function reclaimLocalVram() {
  if (isReclaiming) {
    return;
  }
  isReclaiming = true;
  reclaimStatusText = 'Unloading models & disabling local...';
  renderReclaimButtonState();

  try {
    const res = await fetch('/monitor/reclaim-vram', { method: 'POST' });
    const data = await res.json();
    if (data.unloadedModels && data.unloadedModels.length > 0) {
      reclaimStatusText = '✅ Local disabled & unloaded: ' + data.unloadedModels.join(', ');
    } else {
      reclaimStatusText = '✅ Local disabled (no models were loaded)';
    }
  } catch (e) {
    console.error(e);
    reclaimStatusText = '❌ Failed to reclaim VRAM';
  } finally {
    isReclaiming = false;
    renderReclaimButtonState();
    if (statusResetTimer) {
      clearTimeout(statusResetTimer);
    }
    statusResetTimer = setTimeout(() => {
      reclaimStatusText = '';
      renderReclaimButtonState();
    }, 5000);
    await refresh();
  }
}

function livenessBadge(liveness, enabled) {
  const badge = document.createElement('span');
  let className;
  let statusText;
  if (!enabled) {
    className = 'lv-disabled';
    statusText = 'Disabled';
  } else if (liveness === 'Offline') {
    className = 'lv-offline';
    statusText = 'Offline';
  } else if (liveness === 'Probing') {
    className = 'lv-probing';
    statusText = 'Probing';
  } else {
    className = 'lv-online';
    statusText = 'Online';
  }
  badge.className = 'lv-badge ' + className;
  const dot = document.createElement('span');
  dot.className = 'dot';
  badge.appendChild(dot);
  badge.appendChild(document.createTextNode(statusText));
  return badge;
}

function renderFleet(fleet) {
  const tbody = document.getElementById('fleetRows');
  clear(tbody);
  for (const t of fleet) {
    const tr = document.createElement('tr');

    tr.appendChild(targetCell(t.name));

    const urlCell = document.createElement('td');
    urlCell.textContent = t.url || '-';
    tr.appendChild(urlCell);

    tr.appendChild(td(t.hardwareProfile || '-'));

    const livenessCell = document.createElement('td');
    livenessCell.appendChild(livenessBadge(t.liveness, t.enabled));
    tr.appendChild(livenessCell);

    tr.appendChild(td(t.activeModel || '-'));
    tr.appendChild(td(String(t.inFlightRequests), 'num'));
    tr.appendChild(td(t.contextAllocation || '-'));

    const toggleCell = document.createElement('td');
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = t.enabled;
    checkbox.addEventListener('change', () => toggleTarget(t.name, checkbox.checked, fleet));
    toggleCell.appendChild(checkbox);
    tr.appendChild(toggleCell);

    tbody.appendChild(tr);
  }
}

async function refresh() {
  try {
    const res = await fetch('/monitor/api');
    const data = await res.json();

    renderFleet(data.fleet);
    renderReclaimButtonState();

    renderStatistics('statsRows', data.statistics.today, data.statistics.last7Days);
    renderUsageRatio(data.statistics.last7Days);
    renderCost(data.cost, data.cloudEnabled);

    const inProgressRows = document.getElementById('inProgressRows');
    clear(inProgressRows);
    if (data.inProgress.length === 0) {
      const emptyCell = td('No request in progress');
      emptyCell.className = 'empty';
      emptyCell.colSpan = 4;
      addRow(inProgressRows, [emptyCell]);
    } else {
      for (const r of data.inProgress) {
        addRow(inProgressRows, [
          targetCell(r.target),
          td(r.model),
          td(tokens(r.estimatedPromptTokens), 'num'),
          td((r.elapsedMs / 1000).toFixed(1) + 's', 'num')
        ], 'pending');
      }
    }

    const rows = document.getElementById('rows');
    clear(rows);
    for (const r of data.requests) {
      const time = new Date(r.timestamp).toLocaleTimeString();
      addRow(rows, [
        td(time, 'num'),
        targetCell(r.target),
        td(r.model),
        td(tokens(r.estimatedPromptTokens) + ' / ' + tokens(r.actualPromptTokens), 'num'),
        td(tokens(r.actualResponseTokens), 'num'),
        td((r.elapsedMs / 1000).toFixed(1) + 's', 'num'),
        td(r.statusCode, 'num')
      ], r.success ? null : 'fail');
    }

    renderOverhead(data.overhead);
  } catch (e) {
    console.error(e);
  }
}

function renderOverhead(overhead) {
  const container = document.getElementById('overheadContent');
  if (!container) return;

  if (!overhead) {
    container.innerHTML = '<span class="empty">No overhead recommendation data available.</span>';
    return;
  }

  if (!overhead.overallObservedFactor || overhead.sampleCount === 0) {
    container.innerHTML = `<span class="empty">💡 No completion requests with actual token metrics recorded yet. Recommendations will appear here once Ollama completes requests (Current configured: <strong>${overhead.configuredFactor}</strong>, Estimator: <strong>${overhead.estimator}</strong>).</span>`;
    return;
  }

  const factor = overhead.overallObservedFactor.toFixed(2);
  const diff = Math.round((overhead.overallObservedFactor - overhead.configuredFactor) * 100) / 100;
  const diffText = Math.abs(diff) < 0.005 ? 'matches current configuration' : (diff > 0 ? `+${diff.toFixed(2)} above configured` : `${diff.toFixed(2)} below configured`);

  let html = `<div style="margin-bottom: 0.4rem;">
    💡 <strong>Observed overhead factor:</strong> <span style="font-size: 1.15rem; font-weight: 700; color: #34e0a1; margin-left: 4px;">${factor}</span>
    <span style="color: #888; font-size: 0.82rem; margin-left: 8px;">(${diffText} ${overhead.configuredFactor}, based on ${overhead.sampleCount} request${overhead.sampleCount > 1 ? 's' : ''}, Estimator: <strong>${overhead.estimator}</strong>)</span>
  </div>
  <div style="font-size: 0.85rem; color: #bbb;">
    Recommended setting for <code>config/config.json</code> (or <code>appsettings.json</code> factory defaults): <code style="background: #181818; padding: 2px 6px; border-radius: 4px; color: #3f9eff;">"TokenEstimationOverheadFactor": ${factor}</code>
  </div>`;

  if (overhead.models && overhead.models.length > 1) {
    html += `<div style="margin-top: 0.6rem; font-size: 0.82rem; color: #aaa;"><strong>Per-model observed factors:</strong> `;
    html += overhead.models.map(m => `<span>${m.model}: <strong style="color: #ddd;">${m.observedFactor.toFixed(2)}</strong> (${m.sampleCount} req)</span>`).join(' &bull; ');
    html += `</div>`;
  }

  container.innerHTML = html;
}

async function toggleTarget(name, checked, fleet) {
  // Post the full fleet enabled-state object with the toggled key updated.
  const updated = {};
  for (const t of fleet) {
    updated[t.name] = t.enabled;
  }
  updated[name] = checked;
  try {
    await fetch('/targets', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(updated)
    });
  } catch (e) {
    console.error(e);
  } finally {
    refresh();
  }
}

document.getElementById('reclaimBtn').addEventListener('click', reclaimLocalVram);

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";

    public static async Task<MonitorStateResponse> BuildMonitorStateResponseAsync(
        IActivityMonitorService activityMonitor,
        IActivityStatisticsService activityStatistics,
        IOptions<OllamaRouterOptions> options,
        ITargetAvailabilityService targetAvailability,
        IModelCatalogCacheService modelCatalogCache,
        CancellationToken cancellationToken = default,
        bool includeOverhead = true,
        ITargetLivenessService? liveness = null,
        ITargetExecutionTracker? executionTracker = null,
        IContextAutoShrinkManager? contextShrinkManager = null)
    {
        var snapshot = activityMonitor.GetSnapshot();
        var statistics = activityStatistics.GetSnapshot();
        var now = DateTimeOffset.UtcNow;
        var cloudEnabled = options.Value.IsCloudOverflowConfigured();
        var targets = targetAvailability.GetSnapshot();

        var effectiveTargets = options.Value.GetEffectiveTargets();
        var livenessEntries = liveness?.GetEntries();
        var leaseSnapshot = contextShrinkManager?.GetLeaseSnapshot();

        // Ordered fleet list: configured targets first (configuration order), Cloud always last.
        // (Invalid Targets entries were already warned about at startup; this read-only path
        // uses the default no-logger overload to avoid repeated warnings on every request.)
        var fleet = new List<MonitorFleetTargetItem>(effectiveTargets.Count + 1);
        var periodByName = new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in effectiveTargets)
        {
            var name = target.Name!;
            var enabled = targets.Flags.TryGetValue((RoutingTarget)name, out var targetEnabled) ? targetEnabled : true;

            // Liveness: prefer the raw (non-TTL) liveness state machine; fall back to the
            // catalog-based check when the liveness service is not registered.
            string livenessText;
            if (livenessEntries is { } entries && entries.TryGetValue((RoutingTarget)name, out var entry))
            {
                livenessText = entry.State switch
                {
                    TargetLivenessState.Online => "Online",
                    TargetLivenessState.Offline => "Offline",
                    _ => "Probing"
                };
            }
            else
            {
                livenessText = !string.IsNullOrWhiteSpace(target.Url) &&
                               await modelCatalogCache.HasAnyModelsAsync(target.Url, cancellationToken)
                    ? "Online"
                    : "Offline";
            }

            var executionState = executionTracker?.GetState((RoutingTarget)name);
            var activeModel = executionState?.LoadedModels.FirstOrDefault()?.Name;
            var inFlight = executionState?.InFlightRequests ?? 0;

            var hardwareProfile = target.MaxVramMB.HasValue
                ? $"{target.MaxVramMB.Value} MB"
                : target.HardwareHint;

            // Append the configured speed factor to the HW cell (e.g. "16gb,2.0x") only when it
            // is above the fleet baseline (1.0); the TUI HW column is 9-wide to hold "16gb,2.0x".
            var effectiveSpeedFactor = target.GetEffectiveSpeedFactor();
            if (effectiveSpeedFactor > 1.0)
            {
                hardwareProfile = $"{hardwareProfile},{effectiveSpeedFactor.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}x";
            }

            fleet.Add(new MonitorFleetTargetItem(
                Name: name,
                Url: target.Url,
                Enabled: enabled,
                Liveness: livenessText,
                ActiveModel: activeModel,
                InFlightRequests: inFlight,
                HardwareProfile: hardwareProfile,
                ContextAllocation: FormatContextAllocation((RoutingTarget)name, leaseSnapshot)));

            periodByName[name] = statistics.Last7Days.GetValueOrDefault((RoutingTarget)name) ?? new ActivityStatisticsTotals(0, 0, 0);
        }

        // The Cloud pseudo-target is never probed/tracked: it is "Online" iff cloud is enabled,
        // never has a loaded model or in-flight requests, and never carries a context lease.
        var cloudFlag = targets.Flags.TryGetValue(RoutingTarget.Cloud, out var cloudEnabledFlag) ? cloudEnabledFlag : cloudEnabled;
        fleet.Add(new MonitorFleetTargetItem(
            Name: RoutingTarget.Cloud.Value,
            Url: null,
            Enabled: cloudFlag,
            Liveness: cloudEnabled ? "Online" : "Offline",
            ActiveModel: null,
            InFlightRequests: 0,
            HardwareProfile: null,
            ContextAllocation: "baseline"));
        periodByName[RoutingTarget.Cloud.Value] = statistics.Last7Days.GetValueOrDefault(RoutingTarget.Cloud) ?? new ActivityStatisticsTotals(0, 0, 0);

        var todayByName = new Dictionary<string, ActivityStatisticsTotals>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in periodByName.Keys)
        {
            todayByName[name] = statistics.Today.GetValueOrDefault((RoutingTarget)name) ?? new ActivityStatisticsTotals(0, 0, 0);
        }
        todayByName["total"] = statistics.TodayTotal;
        periodByName["total"] = statistics.Last7DaysTotal;

        return new MonitorStateResponse(
            cloudEnabled,
            fleet,
            new MonitorStatisticsStatus(todayByName, periodByName),
            snapshot.InProgressRequests.Select(r => new MonitorInProgressItem(
                r.Target.ToString(),
                r.Model,
                r.EstimatedPromptTokens,
                (now - r.StartedAt).TotalMilliseconds)).ToList(),
            snapshot.RecentRequests.Select(r => new MonitorRecentRequestItem(
                r.Timestamp,
                r.Target.ToString(),
                r.Model,
                r.EstimatedPromptTokens,
                r.ActualPromptTokens,
                r.ActualResponseTokens,
                r.ElapsedMilliseconds,
                r.StatusCode,
                r.Success,
                r.RawPromptTokens)).ToList(),
            CalculateCostStatus(statistics.Last7Days, options.Value.Pricing),
            includeOverhead
                ? CalculateOverheadStatus(snapshot.RecentRequests, options.Value.TokenEstimationOverheadFactor, options.Value.TokenEstimator)
                : null);
    }

    // Renders the current context allocation for one fleet target from the lease snapshot:
    // any expanded lease -> "<maxBucket>k (expanded)"; a single lease at baseline ->
    // "<bucket>k (baseline)"; no leases at all -> "-" (baseline implied). Cloud is "baseline".
    private static string FormatContextAllocation(
        RoutingTarget target,
        IReadOnlyDictionary<RoutingTarget, IReadOnlyList<ContextLeaseView>>? leaseSnapshot)
    {
        if (target == RoutingTarget.Cloud || leaseSnapshot is null ||
            !leaseSnapshot.TryGetValue(target, out var leases) || leases.Count == 0)
        {
            return target == RoutingTarget.Cloud ? "baseline" : "-";
        }

        if (leases.Any(lease => lease.IsExpanded))
        {
            return $"{FormatBucketTokens(leases.Max(lease => lease.ActiveBucket))} (expanded)";
        }

        return $"{FormatBucketTokens(leases[0].ActiveBucket)} (baseline)";
    }

    private static string FormatBucketTokens(int tokens)
    {
        if (tokens >= 1_000_000)
        {
            return (tokens / 1_000_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "M";
        }
        return tokens >= 1_000
            ? (tokens / 1_000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "k"
            : tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static MonitorCostStatus? CalculateCostStatus(
        IReadOnlyDictionary<RoutingTarget, ActivityStatisticsTotals> last7Days,
        PricingOptions? pricing)
    {
        if (pricing == null || !pricing.IsConfigured)
        {
            return null;
        }

        var effectiveInputPrice = pricing.PromptPricePerMillion ?? pricing.PricePerMillion ?? 0.0;
        var effectiveOutputPrice = pricing.CompletionPricePerMillion ?? pricing.PricePerMillion ?? 0.0;

        // Per-target savings: every NON-Cloud target's tokens valued at the effective cloud
        // prices (the same 4-decimal rounding as the totals); the Cloud overflow is excluded
        // (it is a cost, not a saving).
        var perTargetSavings = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var estimatedSavings = 0.0;
        foreach (var (target, stats) in last7Days)
        {
            if (target == RoutingTarget.Cloud)
            {
                continue;
            }

            var targetSavings = (stats.InputTokens / 1_000_000.0 * effectiveInputPrice) +
                                (stats.OutputTokens / 1_000_000.0 * effectiveOutputPrice);
            targetSavings = Math.Round(targetSavings, 4);
            perTargetSavings[target.Value] = targetSavings;
            estimatedSavings += targetSavings;
        }

        var cloudStats = last7Days.GetValueOrDefault(RoutingTarget.Cloud) ?? new ActivityStatisticsTotals(0, 0, 0);
        var cloudOverflowCost = (cloudStats.InputTokens / 1_000_000.0 * effectiveInputPrice) +
                                (cloudStats.OutputTokens / 1_000_000.0 * effectiveOutputPrice);

        return new MonitorCostStatus(
            pricing.Currency,
            Math.Round(estimatedSavings, 4),
            Math.Round(cloudOverflowCost, 4),
            perTargetSavings);
    }

    public static MonitorOverheadStatus CalculateOverheadStatus(
        IReadOnlyList<ActivityLogEntry> recentRequests,
        double configuredFactor,
        string estimator)
    {
        var validSamples = recentRequests
            .Where(r => r.Success && r.ActualPromptTokens.HasValue && r.ActualPromptTokens.Value > 0)
            .Select(r =>
            {
                int raw = r.RawPromptTokens.GetValueOrDefault(0);
                if (raw <= 0)
                {
                    raw = (int)Math.Round(r.EstimatedPromptTokens / (configuredFactor > 0 ? configuredFactor : 1.0));
                }
                return (r.Model, Raw: raw, Actual: r.ActualPromptTokens!.Value);
            })
            .Where(x => x.Raw > 0)
            .ToList();

        if (validSamples.Count == 0)
        {
            return new MonitorOverheadStatus(configuredFactor, estimator, null, 0, Array.Empty<MonitorOverheadRecommendation>());
        }

        var modelGroups = validSamples
            .GroupBy(x => x.Model)
            .Select(g =>
            {
                long sumRaw = g.Sum(x => (long)x.Raw);
                long sumActual = g.Sum(x => (long)x.Actual);
                double factor = sumRaw > 0 ? Math.Round((double)sumActual / sumRaw, 2) : 1.0;
                return new MonitorOverheadRecommendation(g.Key, factor, g.Count());
            })
            .OrderByDescending(m => m.SampleCount)
            .ToList();

        long totalRaw = validSamples.Sum(x => (long)x.Raw);
        long totalActual = validSamples.Sum(x => (long)x.Actual);
        double overallFactor = totalRaw > 0 ? Math.Round((double)totalActual / totalRaw, 2) : 1.0;

        return new MonitorOverheadStatus(configuredFactor, estimator, overallFactor, validSamples.Count, modelGroups);
    }
}

/// <summary>
/// Request payload for <c>POST /targets</c>: a flat JSON object of target-name to enabled flag
/// (e.g. {"Local": true, "Remote": false, "Cloud": true}); keys are matched case-insensitively
/// against the fleet target names, so legacy lowercase clients keep working.
/// </summary>
public sealed class TargetsUpdateRequest
{
    [JsonExtensionData]
    public Dictionary<string, object>? Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Interprets the flat extension-data object as a target-name to enabled-flag dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, bool> AsFlags() =>
        (Values ?? new Dictionary<string, object>())
            .Select(pair => (Key: pair.Key, Value: pair.Value))
            .Where(pair => pair.Value is bool || pair.Value is System.Text.Json.JsonElement)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value is bool b ? b : ((System.Text.Json.JsonElement)pair.Value!).GetBoolean(),
                StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Response payload for <c>POST /targets</c>: the same flat target-name to enabled flag object
/// that was applied.
/// </summary>
public sealed class TargetsUpdateResponse
{
    [JsonExtensionData]
    public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TargetsUpdateResponse() { }

    public TargetsUpdateResponse(IReadOnlyDictionary<string, bool> values)
    {
        foreach (var (key, value) in values)
        {
            Values[key] = value;
        }
    }
}

public sealed record ReclaimVramResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("unloadedModels")] IReadOnlyList<string> UnloadedModels,
    [property: JsonPropertyName("localDisabled")] bool LocalDisabled);

public sealed record MonitorCostStatus(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("estimatedSavings")] double EstimatedSavings,
    [property: JsonPropertyName("cloudOverflowCost")] double CloudOverflowCost,
    [property: JsonPropertyName("perTargetSavings")] IReadOnlyDictionary<string, double> PerTargetSavings);

/// <summary>
/// One row of the ordered multi-target fleet view (configured targets first, Cloud last):
/// identity, enablement, liveness (Online/Offline/Probing), the currently loaded model and
/// in-flight request count (execution tracker; Cloud always null/0), the hardware profile,
/// and the current context bucket allocation (auto-shrink lease; Cloud always "baseline").
/// </summary>
public sealed record MonitorFleetTargetItem(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("liveness")] string Liveness,
    [property: JsonPropertyName("inFlightRequests")] int InFlightRequests,
    [property: JsonPropertyName("contextAllocation")] string ContextAllocation,
    [property: JsonPropertyName("url")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null,
    [property: JsonPropertyName("activeModel")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActiveModel = null,
    [property: JsonPropertyName("hardwareProfile")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HardwareProfile = null);

public sealed record MonitorStateResponse(
    [property: JsonPropertyName("cloudEnabled")] bool CloudEnabled,
    [property: JsonPropertyName("fleet")] IReadOnlyList<MonitorFleetTargetItem> Fleet,
    [property: JsonPropertyName("statistics")] MonitorStatisticsStatus Statistics,
    [property: JsonPropertyName("inProgress")] IReadOnlyList<MonitorInProgressItem> InProgress,
    [property: JsonPropertyName("requests")] IReadOnlyList<MonitorRecentRequestItem> Requests,
    [property: JsonPropertyName("cost")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitorCostStatus? Cost = null,
    [property: JsonPropertyName("overhead")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitorOverheadStatus? Overhead = null);

/// <summary>
/// Per-period usage statistics keyed by display target name, plus a reserved "total" key.
/// </summary>
public sealed record MonitorStatisticsStatus(
    [property: JsonPropertyName("today")] IReadOnlyDictionary<string, ActivityStatisticsTotals> Today,
    [property: JsonPropertyName("last7Days")] IReadOnlyDictionary<string, ActivityStatisticsTotals> Last7Days);

public sealed record MonitorInProgressItem(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("estimatedPromptTokens")] int EstimatedPromptTokens,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs);

public sealed record MonitorRecentRequestItem(
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("estimatedPromptTokens")] int EstimatedPromptTokens,
    [property: JsonPropertyName("actualPromptTokens")] int? ActualPromptTokens,
    [property: JsonPropertyName("actualResponseTokens")] int? ActualResponseTokens,
    [property: JsonPropertyName("elapsedMs")] double ElapsedMs,
    [property: JsonPropertyName("statusCode")] int? StatusCode,
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("rawPromptTokens")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RawPromptTokens = null);

public sealed record MonitorOverheadRecommendation(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("observedFactor")] double ObservedFactor,
    [property: JsonPropertyName("sampleCount")] int SampleCount);

public sealed record MonitorOverheadStatus(
    [property: JsonPropertyName("configuredFactor")] double ConfiguredFactor,
    [property: JsonPropertyName("estimator")] string Estimator,
    [property: JsonPropertyName("overallObservedFactor")] double? OverallObservedFactor,
    [property: JsonPropertyName("sampleCount")] int SampleCount,
    [property: JsonPropertyName("models")] IReadOnlyList<MonitorOverheadRecommendation> Models);
