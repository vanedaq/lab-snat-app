using Azure.Data.Tables;
using StackExchange.Redis;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// ── Health check basico ──────────────────────────────────────────────────────
app.MapGet("/", () => Results.Ok(new {
    status = "running",
    app = "Lab SNAT - Prometec",
    timestamp = DateTime.UtcNow,
    endpoints = new[] { "/test", "/storage", "/redis", "/full" }
}));

// ── Test Storage ─────────────────────────────────────────────────────────────
app.MapGet("/storage", async (IConfiguration config) =>
{
    var connStr = config["StorageConnectionString"];
    if (string.IsNullOrEmpty(connStr))
        return Results.BadRequest(new { error = "StorageConnectionString no configurado" });

    var sw = Stopwatch.StartNew();
    try
    {
        var client = new TableClient(connStr, "timbrados");
        await client.CreateIfNotExistsAsync();

        var entity = new TableEntity("lab", Guid.NewGuid().ToString())
        {
            { "timestamp", DateTime.UtcNow.ToString("o") },
            { "source", "lab-snat-app" },
            { "test", "storage-write" }
        };

        await client.AddEntityAsync(entity);
        sw.Stop();

        var resolvedIp = await ResolveHostAsync(connStr);

        return Results.Ok(new
        {
            status = "OK",
            operation = "Table Storage Write",
            duration_ms = sw.ElapsedMilliseconds,
            resolved_ip = resolvedIp,
            is_private = IsPrivateIp(resolvedIp),
            entity_id = entity.RowKey,
            timestamp = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        sw.Stop();
        return Results.Ok(new { status = "ERROR", error = ex.Message, duration_ms = sw.ElapsedMilliseconds });
    }
});

// ── Test Redis ───────────────────────────────────────────────────────────────
app.MapGet("/redis", async (IConfiguration config) =>
{
    var connStr = config["RedisConnectionString"];
    if (string.IsNullOrEmpty(connStr))
        return Results.BadRequest(new { error = "RedisConnectionString no configurado" });

    var sw = Stopwatch.StartNew();
    try
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(connStr);
        var db = redis.GetDatabase();

        var key = $"lab:test:{Guid.NewGuid()}";
        var value = $"test-value-{DateTime.UtcNow:o}";

        await db.StringSetAsync(key, value, TimeSpan.FromMinutes(5));
        var retrieved = await db.StringGetAsync(key);
        sw.Stop();

        var endpoint = redis.GetEndPoints().FirstOrDefault()?.ToString() ?? "unknown";
        var host = endpoint.Split(':')[0];
        var resolvedIp = await ResolveHostAsync(host);

        return Results.Ok(new
        {
            status = "OK",
            operation = "Redis Set/Get",
            duration_ms = sw.ElapsedMilliseconds,
            key = key,
            value_match = retrieved == value,
            redis_endpoint = endpoint,
            resolved_ip = resolvedIp,
            is_private = IsPrivateIp(resolvedIp),
            timestamp = DateTime.UtcNow
        });
    }
    catch (Exception ex)
    {
        sw.Stop();
        return Results.Ok(new { status = "ERROR", error = ex.Message, duration_ms = sw.ElapsedMilliseconds });
    }
});

// ── Test completo (simula un timbrado) ───────────────────────────────────────
app.MapGet("/full", async (IConfiguration config) =>
{
    var storageConn = config["StorageConnectionString"];
    var redisConn   = config["RedisConnectionString"];
    var results     = new Dictionary<string, object>();
    var totalSw     = Stopwatch.StartNew();

    // 1. Redis - verificar sesion en cache
    var swRedis = Stopwatch.StartNew();
    string redisStatus = "skipped", redisIp = "N/A";
    bool redisPrivate = false;
    try
    {
        if (!string.IsNullOrEmpty(redisConn))
        {
            var redis = await ConnectionMultiplexer.ConnectAsync(redisConn);
            var db = redis.GetDatabase();
            await db.StringSetAsync($"session:{Guid.NewGuid()}", "active", TimeSpan.FromMinutes(30));
            redisStatus = "OK";
            var host = redis.GetEndPoints().FirstOrDefault()?.ToString()?.Split(':')[0] ?? "";
            redisIp = await ResolveHostAsync(host);
            redisPrivate = IsPrivateIp(redisIp);
        }
    }
    catch (Exception ex) { redisStatus = $"ERROR: {ex.Message}"; }
    swRedis.Stop();

    results["redis"] = new { status = redisStatus, duration_ms = swRedis.ElapsedMilliseconds,
        resolved_ip = redisIp, is_private = redisPrivate };

    // 2. Storage - registrar timbrado
    var swStorage = Stopwatch.StartNew();
    string storageStatus = "skipped", storageIp = "N/A";
    bool storagePrivate = false;
    try
    {
        if (!string.IsNullOrEmpty(storageConn))
        {
            var client = new TableClient(storageConn, "timbrados");
            await client.CreateIfNotExistsAsync();
            await client.AddEntityAsync(new TableEntity("timbrado", Guid.NewGuid().ToString())
            {
                { "timestamp", DateTime.UtcNow.ToString("o") },
                { "folio", $"LAB-{DateTime.UtcNow:yyyyMMddHHmmssfff}" },
                { "status", "timbrado" }
            });
            storageStatus = "OK";
            storageIp = await ResolveStorageIpAsync(storageConn);
            storagePrivate = IsPrivateIp(storageIp);
        }
    }
    catch (Exception ex) { storageStatus = $"ERROR: {ex.Message}"; }
    swStorage.Stop();

    results["storage"] = new { status = storageStatus, duration_ms = swStorage.ElapsedMilliseconds,
        resolved_ip = storageIp, is_private = storagePrivate };

    totalSw.Stop();

    return Results.Ok(new
    {
        status = results.Values.All(r => r.ToString()!.Contains("OK") || r.ToString()!.Contains("skipped")) ? "OK" : "PARTIAL",
        total_duration_ms = totalSw.ElapsedMilliseconds,
        snat_risk = !storagePrivate ? "ALTO - Storage usa IP publica (consume puertos SNAT)" : "BAJO - Storage usa red privada",
        results,
        timestamp = DateTime.UtcNow
    });
});

app.Run();

// ── Helpers ──────────────────────────────────────────────────────────────────
static async Task<string> ResolveHostAsync(string hostOrConnStr)
{
    try
    {
        var host = hostOrConnStr;
        if (hostOrConnStr.Contains("AccountName=") || hostOrConnStr.Contains("DefaultEndpoints"))
        {
            host = await ResolveStorageIpAsync(hostOrConnStr);
            return host;
        }
        var ips = await Dns.GetHostAddressesAsync(host);
        return ips.FirstOrDefault()?.ToString() ?? "no-resolve";
    }
    catch { return "error-resolving"; }
}

static async Task<string> ResolveStorageIpAsync(string connStr)
{
    try
    {
        var parts = connStr.Split(';');
        var accountName = parts.FirstOrDefault(p => p.StartsWith("AccountName="))?.Split('=')[1];
        if (accountName == null) return "unknown";
        var ips = await Dns.GetHostAddressesAsync($"{accountName}.table.core.windows.net");
        return ips.FirstOrDefault()?.ToString() ?? "no-resolve";
    }
    catch { return "error-resolving"; }
}

static bool IsPrivateIp(string ip)
{
    if (!IPAddress.TryParse(ip, out var addr)) return false;
    var bytes = addr.GetAddressBytes();
    return bytes[0] == 10 ||
           (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
           (bytes[0] == 192 && bytes[1] == 168);
}
