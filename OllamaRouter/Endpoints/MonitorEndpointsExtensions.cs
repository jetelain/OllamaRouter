using Microsoft.Extensions.Options;
using OllamaRouter.Options;
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
        app.MapGet("/monitor/api", (IActivityMonitorService activityMonitor, IActivityStatisticsService activityStatistics, IOptions<OllamaRouterOptions> options, ITargetAvailabilityService targetAvailability) =>
        {
            var snapshot = activityMonitor.GetSnapshot();
            var statistics = activityStatistics.GetSnapshot();
            var now = DateTimeOffset.UtcNow;
            var cloudEnabled = options.Value.Models.Values.Any(m => !string.IsNullOrEmpty(m.CloudModel));
            var targets = targetAvailability.GetSnapshot();

            return Results.Json(new
            {
                cloudEnabled,
                targets = new
                {
                    local = targets.Local,
                    remote = targets.Remote,
                    cloud = targets.Cloud
                },
                busy = new
                {
                    local = snapshot.Busy.GetValueOrDefault(RoutingTarget.Local),
                    remote = snapshot.Busy.GetValueOrDefault(RoutingTarget.Remote),
                    cloud = snapshot.Busy.GetValueOrDefault(RoutingTarget.Cloud)
                },
                statistics = new
                {
                    today = new
                    {
                        local = statistics.Today[RoutingTarget.Local],
                        remote = statistics.Today[RoutingTarget.Remote],
                        cloud = statistics.Today[RoutingTarget.Cloud],
                        total = statistics.TodayTotal
                    },
                    last7Days = new
                    {
                        local = statistics.Last7Days[RoutingTarget.Local],
                        remote = statistics.Last7Days[RoutingTarget.Remote],
                        cloud = statistics.Last7Days[RoutingTarget.Cloud],
                        total = statistics.Last7DaysTotal
                    }
                },
                inProgress = snapshot.InProgressRequests.Select(r => new
                {
                    target = r.Target.ToString(),
                    model = r.Model,
                    estimatedPromptTokens = r.EstimatedPromptTokens,
                    elapsedMs = (now - r.StartedAt).TotalMilliseconds
                }),
                requests = snapshot.RecentRequests.Select(r => new
                {
                    timestamp = r.Timestamp,
                    target = r.Target.ToString(),
                    model = r.Model,
                    estimatedPromptTokens = r.EstimatedPromptTokens,
                    actualPromptTokens = r.ActualPromptTokens,
                    actualResponseTokens = r.ActualResponseTokens,
                    elapsedMs = r.ElapsedMilliseconds,
                    statusCode = r.StatusCode,
                    success = r.Success
                })
            });
        });

        app.MapPost("/targets", (TargetsUpdateRequest request, ITargetAvailabilityService targetAvailability) =>
        {
            targetAvailability.Update(request.Local, request.Remote, request.Cloud);
            return Results.Ok(new { request.Local, request.Remote, request.Cloud });
        });

        app.MapGet("/monitor", () => Results.Content(MonitorPageHtml, "text/html"));

        return app;
    }

    /// <summary>
    /// Request payload for <c>POST /targets</c>.
    /// </summary>
    public sealed record TargetsUpdateRequest(bool Local, bool Remote, bool Cloud);

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
  .instances { display: flex; gap: 1rem; margin-bottom: 1.5rem; }
  .card { padding: 0.75rem 1.25rem; border-radius: 8px; min-width: 140px; background: #2b2b2b; }
  .card.busy { background: #5a3d00; border: 1px solid #ffb300; }
  .card.idle { background: #1f3d24; border: 1px solid #3ba55c; }
  .card.disabled { background: #3a1f1f; border: 1px solid #b33333; }
  .card .toggle { display: flex; align-items: center; margin-top: 0.4rem; font-size: 0.8rem; color: #bbb; }
  .card .toggle input { margin-right: 6px; }
  .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 6px; }
  .busy .dot { background: #ffb300; }
  .idle .dot { background: #3ba55c; }
  .disabled .dot { background: #b33333; }
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
  .target-Local { color: #6bc4ff; }
  .target-Remote { color: #5ce8b5; }
  .target-Cloud { color: #c48bff; }
  .empty { color: #777; font-style: italic; padding: 0.5rem 0.75rem; }
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
      const div = document.createElement('div');
      div.className = 'card ' + (!enabled ? 'disabled' : (busy ? 'busy' : 'idle'));
      const dot = document.createElement('span');
      dot.className = 'dot';
      div.appendChild(dot);
      const targetName = name.charAt(0).toUpperCase() + name.slice(1);
            const label = (targetIcons[targetName] + ' ') + targetName + ': ' + (!enabled ? 'Disabled' : (busy ? 'Busy' : 'Idle'));
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

      instances.appendChild(div);
    }

    renderStatistics('statsRows', data.statistics.today, data.statistics.last7Days, data.cloudEnabled);

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
  } catch (e) {
    console.error(e);
  }
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
}
