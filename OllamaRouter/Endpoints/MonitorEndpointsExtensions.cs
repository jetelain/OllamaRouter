using Microsoft.Extensions.Options;
using OllamaRouter.Options;
using OllamaRouter.Services;

namespace OllamaRouter.Endpoints;

/// <summary>
/// Lightweight, dependency-free monitoring UI: shows which instance (Local/Remote) is currently
/// busy, requests currently in progress, and the recent request history with model, estimated
/// and actual tokens, and elapsed time. It only reads in-memory state from
/// <see cref="IActivityMonitorService"/>, so it has no impact on VRAM.
/// </summary>
public static class MonitorEndpointsExtensions
{
    public static WebApplication MapOllamaMonitorEndpoints(this WebApplication app)
    {
        app.MapGet("/monitor/api", (IActivityMonitorService activityMonitor, IOptions<OllamaRouterOptions> options) =>
        {
            var snapshot = activityMonitor.GetSnapshot();
            var now = DateTimeOffset.UtcNow;
            var cloudEnabled = options.Value.Models.Values.Any(m => !string.IsNullOrEmpty(m.CloudModel));

            return Results.Json(new
            {
                cloudEnabled,
                busy = new
                {
                    local = snapshot.Busy.GetValueOrDefault(RoutingTarget.Local),
                    remote = snapshot.Busy.GetValueOrDefault(RoutingTarget.Remote),
                    cloud = snapshot.Busy.GetValueOrDefault(RoutingTarget.Cloud)
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
  .instances { display: flex; gap: 1rem; margin-bottom: 1.5rem; }
  .card { padding: 0.75rem 1.25rem; border-radius: 8px; min-width: 140px; background: #2b2b2b; }
  .card.busy { background: #5a3d00; border: 1px solid #ffb300; }
  .card.idle { background: #1f3d24; border: 1px solid #3ba55c; }
  .dot { display: inline-block; width: 10px; height: 10px; border-radius: 50%; margin-right: 6px; }
  .busy .dot { background: #ffb300; }
  .idle .dot { background: #3ba55c; }
  table { border-collapse: collapse; width: 100%; }
  th, td { text-align: left; padding: 0.35rem 0.75rem; border-bottom: 1px solid #333; font-size: 0.9rem; }
  th { color: #999; font-weight: 600; }
  tr.fail { color: #ff6b6b; }
  tr.pending { color: #ffd479; }
  .target-Local { color: #6bc4ff; }
  .target-Remote { color: #c48bff; }
  .target-Cloud { color: #5ce8b5; }
  .empty { color: #777; font-style: italic; padding: 0.5rem 0.75rem; }
</style>
</head>
<body>
<h1>OllamaRouter - Activity Monitor</h1>
<div class="instances" id="instances"></div>

<h2>In progress</h2>
<table>
  <thead>
    <tr><th>Target</th><th>Model</th><th>Estimated input tokens</th><th>Running for</th></tr>
  </thead>
  <tbody id="inProgressRows"></tbody>
</table>

<h2>Recent requests</h2>
<table>
  <thead>
    <tr><th>Time</th><th>Target</th><th>Model</th><th>Input tokens (est. / actual)</th><th>Output tokens</th><th>Elapsed</th><th>Status</th></tr>
  </thead>
  <tbody id="rows"></tbody>
</table>
<script>
function td(text) {
  const cell = document.createElement('td');
  cell.textContent = text;
  return cell;
}

function targetCell(target) {
  const cell = document.createElement('td');
  const span = document.createElement('span');
  span.className = 'target-' + target;
  span.textContent = target;
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
      const div = document.createElement('div');
      div.className = 'card ' + (busy ? 'busy' : 'idle');
      const dot = document.createElement('span');
      dot.className = 'dot';
      div.appendChild(dot);
      div.appendChild(document.createTextNode(name.charAt(0).toUpperCase() + name.slice(1) + ': ' + (busy ? 'Busy' : 'Idle')));
      instances.appendChild(div);
    }

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
          td(r.estimatedPromptTokens),
          td((r.elapsedMs / 1000).toFixed(1) + 's')
        ], 'pending');
      }
    }

    const rows = document.getElementById('rows');
    clear(rows);
    for (const r of data.requests) {
      const time = new Date(r.timestamp).toLocaleTimeString();
      const inputTokens = r.estimatedPromptTokens + ' / ' + (r.actualPromptTokens ?? '-');
      addRow(rows, [
        td(time),
        targetCell(r.target),
        td(r.model),
        td(inputTokens),
        td(r.actualResponseTokens ?? '-'),
        td((r.elapsedMs / 1000).toFixed(1) + 's'),
        td(r.statusCode)
      ], r.success ? null : 'fail');
    }
  } catch (e) {
    console.error(e);
  }
}

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}
