using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static class DashboardEndpoints
{
    /// <summary>
    /// Adds log browser + dashboard endpoints that read CSV files in logsDir.
    /// </summary>
    public static void MapDashboardAndLogs(this WebApplication app, string logsDir)
    {
        // List CSVs
        app.MapGet("/logs", () =>
        {
            if (!Directory.Exists(logsDir)) return Results.Json(Array.Empty<object>());
            var list = Directory.EnumerateFiles(logsDir, "*.csv")
                .OrderByDescending(f => f)
                .Select(f => new
                {
                    file = Path.GetFileName(f),
                    size = new FileInfo(f).Length,
                    modifiedUtc = File.GetLastWriteTimeUtc(f)
                });
            return Results.Json(list);
        });

        // Download CSV
        app.MapGet("/log.csv", (string file) =>
        {
            var path = SafeLogPath(logsDir, file);
            if (path is null || !System.IO.File.Exists(path)) return Results.NotFound();
            var bytes = System.IO.File.ReadAllBytes(path);
            return Results.File(bytes, "text/csv", Path.GetFileName(path));
        });

        // Read CSV -> JSON rows
        app.MapGet("/log.json", (string file) =>
        {
            var path = SafeLogPath(logsDir, file);
            if (path is null || !System.IO.File.Exists(path)) return Results.NotFound();

            var rows = new List<object>(4096);
            foreach (var line in System.IO.File.ReadLines(path).Skip(1)) // skip header
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 5) continue;
                if (!DateTimeOffset.TryParse(parts[0], out var ts)) continue;
                if (!int.TryParse(parts[1], out var bpm)) continue;
                int? batt = int.TryParse(parts[2], out var b) ? b : null;
                int? energy = int.TryParse(parts[3], out var en) ? en : null;
                var rr = parts[4].Split(';', StringSplitOptions.RemoveEmptyEntries)
                                .Select(x => int.TryParse(x, out var n) ? n : (int?)null)
                                .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                rows.Add(new { ts, bpm, battery = batt, energy, rr });
            }
            return Results.Json(rows);
        });

        // Delete CSV (POST)
        app.MapPost("/logs/delete", (string file) =>
        {
            var path = SafeLogPath(logsDir, file);
            if (path is null || !System.IO.File.Exists(path)) return Results.NotFound();
            try { System.IO.File.Delete(path); }
            catch { return Results.StatusCode(500); }
            return Results.Ok(new { deleted = Path.GetFileName(path) });
        });

        // Dashboard HTML (dropdown + chart)
        app.MapGet("/dashboard", () =>
        {
            const string html = @"<!doctype html>
<html lang='en'>
<head>
<meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'/>
<title>XossHrmServer — Dashboard</title>
<style>
  body{font-family:ui-sans-serif,system-ui,-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;margin:24px;background:#0b1220;color:#e8eef7}
  h1{font-size:20px;margin:0 0 10px}
  .row{display:flex;gap:12px;align-items:center;margin-bottom:12px;flex-wrap:wrap}
  .card{background:#131c31;border:1px solid #22304f;border-radius:12px;padding:16px;box-shadow:0 6px 20px rgba(0,0,0,.2)}
  .muted{opacity:.8}
  button{background:#2c72ff;color:#fff;border:0;border-radius:10px;padding:8px 12px;cursor:pointer}
  input,select{background:#111a2c;color:#e8eef7;border:1px solid #2b3b5d;border-radius:8px;padding:6px 8px}
  canvas{max-width:100%;height:420px}
  a{color:#7fb3ff}
</style>
</head>
<body>
  <div class='card'>
    <h1>Session Dashboard</h1>
    <div class='row'>
      <label for='file'>Dataset:</label>
      <select id='file'></select>
      <button id='reload'>Reload</button>
      <a id='download' href='#' download>Download CSV</a>
      <button id='delete' style='background:#ff3b3b'>Delete</button>
      <span id='meta' class='muted'></span>
    </div>
    <canvas id='chart'></canvas>
  </div>

<script src='https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js'></script>
<script src='https://cdn.jsdelivr.net/npm/luxon@3.4.4/build/global/luxon.min.js'></script>
<script src='https://cdn.jsdelivr.net/npm/chartjs-adapter-luxon@1.3.1/dist/chartjs-adapter-luxon.umd.min.js'></script>
<script>
let chart;

async function loadList(){
  const r = await fetch('/logs');
  const files = await r.json();
  const sel = document.getElementById('file');
  sel.innerHTML = '';
  for(const f of files){
    const opt = document.createElement('option');
    opt.value = f.file;
    opt.textContent = f.file + '  (' + (new Date(f.modifiedUtc)).toLocaleString() + ')';
    sel.appendChild(opt);
  }
  if(files.length){
    sel.value = files[0].file;
    document.getElementById('download').href = '/log.csv?file=' + encodeURIComponent(sel.value);
  }else{
    document.getElementById('download').href = '#';
  }
}

async function loadData(){
  const sel = document.getElementById('file');
  if(!sel.value){ document.getElementById('meta').textContent = 'No logs found.'; return; }
  const r = await fetch('/log.json?file=' + encodeURIComponent(sel.value));
  if(!r.ok){ document.getElementById('meta').textContent = 'Failed to load.'; return; }
  const rows = await r.json();
  const labels = rows.map(p => new Date(p.ts));
  const bpm = rows.map(p => p.bpm);

  const durMin = labels.length ? ((labels.at(-1) - labels[0]) / 60000).toFixed(1) : 0;
  const avg = bpm.length ? (bpm.reduce((a,b)=>a+b,0) / bpm.length).toFixed(1) : 0;
  const min = bpm.length ? Math.min(...bpm) : 0;
  const max = bpm.length ? Math.max(...bpm) : 0;
  document.getElementById('meta').textContent = `points: ${bpm.length} | duration: ${durMin} min | avg: ${avg} bpm | min: ${min} | max: ${max}`;

  if(chart) chart.destroy();
  chart = new Chart(document.getElementById('chart').getContext('2d'), {
    type: 'line',
    data: { labels, datasets: [{ label: 'BPM', data: bpm, tension: .2, pointRadius: 0, borderWidth: 2 }] },
    options: {
      animation: false, normalized: true,
      interaction: { mode: 'index', intersect: false },
      scales: {
        x: { type: 'time', time: { unit: 'minute' }, ticks: { color: '#b8c9f1' }, grid: { color: 'rgba(255,255,255,.06)' } },
        y: { suggestedMin: 50, suggestedMax: 180, ticks: { color: '#b8c9f1' }, grid: { color: 'rgba(255,255,255,.06)' } }
      },
      plugins: { legend: { labels: { color: '#e8eef7' } } }
    }
  });
}

document.getElementById('reload').addEventListener('click', loadData);
document.getElementById('file').addEventListener('change', e => {
  document.getElementById('download').href = '/log.csv?file=' + encodeURIComponent(e.target.value);
  loadData();
});
document.getElementById('delete').addEventListener('click', async () => {
  const sel = document.getElementById('file');
  if(!sel.value) return;
  if(confirm('Delete ' + sel.value + '?')){
    await fetch('/logs/delete?file=' + encodeURIComponent(sel.value), { method: 'POST' });
    await loadList();
    await loadData();
  }
});

loadList().then(loadData);
</script>
</body>
</html>";
            return Results.Text(html, "text/html; charset=utf-8");
        });
    }

    private static string? SafeLogPath(string logsDir, string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        if (file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var path = Path.Combine(logsDir, file);
        try
        {
            var full = Path.GetFullPath(path);
            var fullDir = Path.GetFullPath(logsDir);
            if (!full.StartsWith(fullDir, StringComparison.OrdinalIgnoreCase)) return null;
            return full;
        }
        catch { return null; }
    }
}
