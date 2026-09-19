using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Serialization;
using OllamaRouter.Services;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Lightweight, dependency-free monitoring UI: shows which instance (Local/Remote) is currently
/// busy, requests currently in progress, and the recent request history with model, estimated
/// and actual tokens, and elapsed time. It reads in-memory state from
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
            CancellationToken cancellationToken) =>
        {
            var snapshot = activityMonitor.GetSnapshot();
            var statistics = activityStatistics.GetSnapshot();
            var now = DateTimeOffset.UtcNow;
            var cloudEnabled = options.Value.Models.Values.Any(m => !string.IsNullOrEmpty(m.CloudModel));
            var targets = targetAvailability.GetSnapshot();

            var localOnline = !string.IsNullOrWhiteSpace(options.Value.LocalUrl) &&
                              await modelCatalogCache.HasAnyModelsAsync(options.Value.LocalUrl, cancellationToken);
            var remoteOnline = !string.IsNullOrWhiteSpace(options.Value.RemoteUrl) &&
                               await modelCatalogCache.HasAnyModelsAsync(options.Value.RemoteUrl, cancellationToken);

            var monitorResponse = new MonitorStateResponse(
                cloudEnabled,
                new MonitorTargetsStatus(targets.Local, targets.Remote, targets.Cloud),
                new MonitorTargetsStatus(localOnline, remoteOnline, cloudEnabled),
                new MonitorBusyStatus(
                    snapshot.Busy.GetValueOrDefault(RoutingTarget.Local),
                    snapshot.Busy.GetValueOrDefault(RoutingTarget.Remote),
                    snapshot.Busy.GetValueOrDefault(RoutingTarget.Cloud)),
                new MonitorStatisticsStatus(
                    new MonitorPeriodStatistics(
                        statistics.Today[RoutingTarget.Local],
                        statistics.Today[RoutingTarget.Remote],
                        statistics.Today[RoutingTarget.Cloud],
                        statistics.TodayTotal),
                    new MonitorPeriodStatistics(
                        statistics.Last7Days[RoutingTarget.Local],
                        statistics.Last7Days[RoutingTarget.Remote],
                        statistics.Last7Days[RoutingTarget.Cloud],
                        statistics.Last7DaysTotal)),
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
                CalculateOverheadStatus(snapshot.RecentRequests, options.Value.TokenEstimationOverheadFactor, options.Value.TokenEstimator));

            return Results.Json(monitorResponse, OllamaRouterJsonSerializerContext.Default.MonitorStateResponse);
        });

        app.MapPost("/targets", (TargetsUpdateRequest request, ITargetAvailabilityService targetAvailability) =>
        {
            targetAvailability.Update(request.Local, request.Remote, request.Cloud);
            return Results.Ok(new TargetsUpdateResponse(request.Local, request.Remote, request.Cloud));
        });

        app.MapPost("/monitor/reclaim-vram", async (IOllamaModelCatalogClient catalogClient, ITargetAvailabilityService targetAvailability, IOptions<OllamaRouterOptions> options, CancellationToken cancellationToken) =>
        {
            var targets = targetAvailability.GetSnapshot();
            targetAvailability.Update(local: false, targets.Remote, targets.Cloud);

            var localUrl = options.Value.LocalUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(localUrl))
            {
                return Results.Ok(new ReclaimVramResponse(
                    Success: false,
                    Message: "Local URL is not configured.",
                    Count: 0,
                    UnloadedModels: Array.Empty<string>(),
                    LocalDisabled: true));
            }

            var unloaded = await catalogClient.StopRunningModelsAsync(localUrl, cancellationToken);
            return Results.Ok(new ReclaimVramResponse(
                Success: true,
                Message: null,
                Count: unloaded.Count,
                UnloadedModels: unloaded,
                LocalDisabled: true));
        });

        app.MapGet("/monitor", () => Results.Content(MonitorPageHtml, "text/html"));

        return app;
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
  .instances { display: flex; gap: 1rem; margin-bottom: 1.5rem; align-items: flex-start; }
  .instance-col { display: flex; flex-direction: column; gap: 0.4rem; }
  .card { padding: 0.75rem 1.25rem; border-radius: 8px; min-width: 140px; background: #2b2b2b; }
  .card.busy { background: #5a3d00; border: 1px solid #ffb300; }
  .card.idle { background: #1f3d24; border: 1px solid #3ba55c; }
  .card.disabled { background: #3a1f1f; border: 1px solid #b33333; }
  .card.offline { background: #4a2800; border: 1px solid #e67e22; }
  .card .toggle { display: flex; align-items: center; margin-top: 0.4rem; font-size: 0.8rem; color: #bbb; }
  .card .toggle input { margin-right: 6px; }
  .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 6px; }
  .busy .dot { background: #ffb300; }
  .idle .dot { background: #3ba55c; }
  .disabled .dot { background: #b33333; }
  .offline .dot { background: #e67e22; }
  .btn-reclaim { display: block; padding: 0.4rem 0.6rem; font-size: 0.78rem; font-weight: 500; color: #eee; background: #333; border: 1px solid #555; border-radius: 6px; cursor: pointer; width: 100%; box-sizing: border-box; text-align: center; }
  .btn-reclaim:hover:not(:disabled) { background: #444; border-color: #888; color: #fff; }
  .btn-reclaim:disabled { opacity: 0.6; cursor: not-allowed; }
  .reclaim-status { font-size: 0.75rem; color: #9cdcfe; line-height: 1.2; word-break: break-word; text-align: center; }
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
  .empty { color: #777; font-style: italic; padding: 0.5rem 0.75rem; }
  .stats-summary-row { display: flex; gap: 1rem; margin-top: 1rem; align-items: stretch; }
  .usage-ratio-container { flex: 2; min-width: 0; background: #262626; border-radius: 8px; padding: 0.85rem 1.1rem; border: 1px solid #333; display: flex; flex-direction: column; justify-content: space-between; }
  .progress-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.6rem; font-size: 0.85rem; color: #aaa; }
  .progress-summary { font-size: 0.8rem; color: #888; }
  .progress-bar { display: flex; height: 18px; border-radius: 6px; overflow: hidden; background: #181818; box-shadow: inset 0 1px 3px rgba(0,0,0,0.5); }
  .progress-seg { height: 100%; position: relative; overflow: hidden; transition: width 0.3s ease; }
  .progress-seg + .progress-seg { border-left: 2px solid #181818; }
  .progress-seg-local { background-color: #3f9eff; background-image: repeating-linear-gradient(45deg, rgba(255,255,255,0.22) 0 6px, transparent 6px 12px); }
  .progress-seg-remote { background-color: #34e0a1; background-image: repeating-linear-gradient(45deg, rgba(255,255,255,0.22) 0 6px, transparent 6px 12px); }
  .progress-seg-cloud { background-color: #c48bff; background-image: repeating-linear-gradient(45deg, rgba(255,255,255,0.22) 0 6px, transparent 6px 12px); }
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
<div class="instances" id="instances"></div>

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
      <div class="cost-desc">Saved thanks to local & remote routing</div>
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
    span.textContent = (targetIcons[target] ? targetIcons[target] + ' ' : '') + target;
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

function renderStatistics(tbodyId, today, last7Days, cloudEnabled) {
  const tbody = document.getElementById(tbodyId);
  clear(tbody);
  const names = ['local', 'remote'];
  if (cloudEnabled) {
    names.push('cloud');
  }
  for (const name of names) {
    addRow(tbody, [
      targetCell(name.charAt(0).toUpperCase() + name.slice(1)),
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

function renderUsageRatio(last7Days, cloudEnabled) {
  const localTokens = (last7Days.local ? last7Days.local.inputTokens + last7Days.local.outputTokens : 0);
  const remoteTokens = (last7Days.remote ? last7Days.remote.inputTokens + last7Days.remote.outputTokens : 0);
  const cloudTokens = (cloudEnabled && last7Days.cloud) ? (last7Days.cloud.inputTokens + last7Days.cloud.outputTokens) : 0;
  const totalTokens = localTokens + remoteTokens + cloudTokens;

  const localReq = last7Days.local ? last7Days.local.requests : 0;
  const remoteReq = last7Days.remote ? last7Days.remote.requests : 0;
  const cloudReq = (cloudEnabled && last7Days.cloud) ? last7Days.cloud.requests : 0;

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

  const localPct = (localTokens / totalTokens * 100);
  const remotePct = (remoteTokens / totalTokens * 100);
  const cloudPct = cloudEnabled ? (cloudTokens / totalTokens * 100) : 0;

  function addSeg(className, pct, title) {
    if (pct <= 0) return;
    const seg = document.createElement('div');
    seg.className = 'progress-seg ' + className;
    seg.style.width = pct.toFixed(2) + '%';
    seg.title = title;
    bar.appendChild(seg);
  }

  addSeg('progress-seg-local', localPct, 'Local: ' + localPct.toFixed(1) + '% (' + tokens(localTokens) + ' tokens, ' + num(localReq) + ' req)');
  addSeg('progress-seg-remote', remotePct, 'Remote: ' + remotePct.toFixed(1) + '% (' + tokens(remoteTokens) + ' tokens, ' + num(remoteReq) + ' req)');
  if (cloudEnabled && cloudPct > 0) {
    addSeg('progress-seg-cloud', cloudPct, 'Cloud: ' + cloudPct.toFixed(1) + '% (' + tokens(cloudTokens) + ' tokens, ' + num(cloudReq) + ' req)');
  }

  function addLegend(targetName, icon, pct, tok, req, colorClass) {
    const item = document.createElement('div');
    item.className = 'legend-item';
    const dot = document.createElement('span');
    dot.className = 'legend-dot ' + colorClass;
    item.appendChild(dot);
    const span = document.createElement('span');
    span.textContent = (icon ? icon + ' ' : '') + targetName + ': ' + pct.toFixed(1) + '%';
    item.appendChild(span);
    legend.appendChild(item);
  }

  addLegend('Local', targetIcons.Local, localPct, localTokens, localReq, 'progress-seg-local');
  addLegend('Remote', targetIcons.Remote, remotePct, remoteTokens, remoteReq, 'progress-seg-remote');
  if (cloudEnabled) {
    addLegend('Cloud', targetIcons.Cloud, cloudPct, cloudTokens, cloudReq, 'progress-seg-cloud');
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

  savingsEl.textContent = formatCost(cost.estimatedSavings, cost.currency);
  cloudCostEl.textContent = formatCost(cost.cloudOverflowCost, cost.currency);

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

async function refresh() {
  try {
    const res = await fetch('/monitor/api');
    const data = await res.json();

    const instances = document.getElementById('instances');
    clear(instances);
    for (const name of ['local', 'remote', 'cloud']) {
      if (name === 'cloud' && !data.cloudEnabled) {
        continue;
      }
      const busy = data.busy[name];
      const enabled = data.targets[name];
      const online = data.online ? data.online[name] : true;

      let statusClass = 'idle';
      let statusText = 'Idle';
      if (!enabled) {
        statusClass = 'disabled';
        statusText = 'Disabled';
      } else if (!online) {
        statusClass = 'offline';
        statusText = 'Offline';
      } else if (busy) {
        statusClass = 'busy';
        statusText = 'Busy';
      }

      const div = document.createElement('div');
      div.className = 'card ' + statusClass;
      const dot = document.createElement('span');
      dot.className = 'dot';
      div.appendChild(dot);
      const targetName = name.charAt(0).toUpperCase() + name.slice(1);
      const label = (targetIcons[targetName] + ' ') + targetName + ': ' + statusText;
      div.appendChild(document.createTextNode(label));

      const toggle = document.createElement('label');
      toggle.className = 'toggle';
      const checkbox = document.createElement('input');
      checkbox.type = 'checkbox';
      checkbox.checked = enabled;
      checkbox.addEventListener('change', () => toggleTarget(name, checkbox.checked, data.targets));
      toggle.appendChild(checkbox);
      toggle.appendChild(document.createTextNode('Enabled'));
      div.appendChild(toggle);

      if (name === 'local') {
        const col = document.createElement('div');
        col.className = 'instance-col';
        col.appendChild(div);

        const btn = document.createElement('button');
        btn.id = 'reclaimBtn';
        btn.className = 'btn-reclaim';
        btn.title = 'Unload all running models in local Ollama instance and disable local target (e.g. to play video games)';
        btn.disabled = isReclaiming;
        btn.textContent = isReclaiming ? '⏳ Reclaiming...' : '🎮 Reclaim local VRAM';
        btn.addEventListener('click', reclaimLocalVram);
        col.appendChild(btn);

        const statusDiv = document.createElement('div');
        statusDiv.id = 'reclaimStatus';
        statusDiv.className = 'reclaim-status';
        statusDiv.textContent = reclaimStatusText;
        col.appendChild(statusDiv);

        instances.appendChild(col);
      } else {
        instances.appendChild(div);
      }
    }

    renderStatistics('statsRows', data.statistics.today, data.statistics.last7Days, data.cloudEnabled);
    renderUsageRatio(data.statistics.last7Days, data.cloudEnabled);
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
    Recommended setting for <code>appsettings.json</code>: <code style="background: #181818; padding: 2px 6px; border-radius: 4px; color: #3f9eff;">"TokenEstimationOverheadFactor": ${factor}</code>
  </div>`;

  if (overhead.models && overhead.models.length > 1) {
    html += `<div style="margin-top: 0.6rem; font-size: 0.82rem; color: #aaa;"><strong>Per-model observed factors:</strong> `;
    html += overhead.models.map(m => `<span>${m.model}: <strong style="color: #ddd;">${m.observedFactor.toFixed(2)}</strong> (${m.sampleCount} req)</span>`).join(' &bull; ');
    html += `</div>`;
  }

  container.innerHTML = html;
}

async function toggleTarget(name, checked, currentTargets) {
  const updated = { local: currentTargets.local, remote: currentTargets.remote, cloud: currentTargets.cloud };
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

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
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

        var localStats = last7Days.GetValueOrDefault(RoutingTarget.Local) ?? new ActivityStatisticsTotals(0, 0, 0);
        var remoteStats = last7Days.GetValueOrDefault(RoutingTarget.Remote) ?? new ActivityStatisticsTotals(0, 0, 0);
        var cloudStats = last7Days.GetValueOrDefault(RoutingTarget.Cloud) ?? new ActivityStatisticsTotals(0, 0, 0);

        long localRemoteInputTokens = (long)localStats.InputTokens + remoteStats.InputTokens;
        long localRemoteOutputTokens = (long)localStats.OutputTokens + remoteStats.OutputTokens;

        var estimatedSavings = (localRemoteInputTokens / 1_000_000.0 * effectiveInputPrice) +
                               (localRemoteOutputTokens / 1_000_000.0 * effectiveOutputPrice);

        var cloudOverflowCost = (cloudStats.InputTokens / 1_000_000.0 * effectiveInputPrice) +
                                (cloudStats.OutputTokens / 1_000_000.0 * effectiveOutputPrice);

        return new MonitorCostStatus(
            pricing.Currency,
            Math.Round(estimatedSavings, 4),
            Math.Round(cloudOverflowCost, 4));
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
/// Request payload for <c>POST /targets</c>.
/// </summary>
public sealed record TargetsUpdateRequest(
    [property: JsonPropertyName("local")] bool Local,
    [property: JsonPropertyName("remote")] bool Remote,
    [property: JsonPropertyName("cloud")] bool Cloud);

public sealed record TargetsUpdateResponse(
    [property: JsonPropertyName("local")] bool Local,
    [property: JsonPropertyName("remote")] bool Remote,
    [property: JsonPropertyName("cloud")] bool Cloud);

public sealed record ReclaimVramResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("unloadedModels")] IReadOnlyList<string> UnloadedModels,
    [property: JsonPropertyName("localDisabled")] bool LocalDisabled);

public sealed record MonitorCostStatus(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("estimatedSavings")] double EstimatedSavings,
    [property: JsonPropertyName("cloudOverflowCost")] double CloudOverflowCost);

public sealed record MonitorStateResponse(
    [property: JsonPropertyName("cloudEnabled")] bool CloudEnabled,
    [property: JsonPropertyName("targets")] MonitorTargetsStatus Targets,
    [property: JsonPropertyName("online")] MonitorTargetsStatus Online,
    [property: JsonPropertyName("busy")] MonitorBusyStatus Busy,
    [property: JsonPropertyName("statistics")] MonitorStatisticsStatus Statistics,
    [property: JsonPropertyName("inProgress")] IReadOnlyList<MonitorInProgressItem> InProgress,
    [property: JsonPropertyName("requests")] IReadOnlyList<MonitorRecentRequestItem> Requests,
    [property: JsonPropertyName("cost")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitorCostStatus? Cost = null,
    [property: JsonPropertyName("overhead")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitorOverheadStatus? Overhead = null);

public sealed record MonitorTargetsStatus(
    [property: JsonPropertyName("local")] bool Local,
    [property: JsonPropertyName("remote")] bool Remote,
    [property: JsonPropertyName("cloud")] bool Cloud);

public sealed record MonitorBusyStatus(
    [property: JsonPropertyName("local")] bool Local,
    [property: JsonPropertyName("remote")] bool Remote,
    [property: JsonPropertyName("cloud")] bool Cloud);

public sealed record MonitorStatisticsStatus(
    [property: JsonPropertyName("today")] MonitorPeriodStatistics Today,
    [property: JsonPropertyName("last7Days")] MonitorPeriodStatistics Last7Days);

public sealed record MonitorPeriodStatistics(
    [property: JsonPropertyName("local")] ActivityStatisticsTotals Local,
    [property: JsonPropertyName("remote")] ActivityStatisticsTotals Remote,
    [property: JsonPropertyName("cloud")] ActivityStatisticsTotals Cloud,
    [property: JsonPropertyName("total")] ActivityStatisticsTotals Total);

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
    [property: JsonPropertyName("rawPromptTokens")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RawPromptTokens = null);

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
