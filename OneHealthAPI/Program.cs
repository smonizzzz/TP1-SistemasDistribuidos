using Microsoft.Data.Sqlite;
using System.Linq;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("AllowAll");

// Caminho relativo ao Servidor (mesmo PC, mesma solução)
string dbPath = Path.GetFullPath(Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory,
    "..", "..", "..", "..", "Servidor", "bin", "Debug", "net8.0", "onehealth.db"
));
string connectionString = $"Data Source={dbPath}";

// Coordenadas fixas por cidade/zona
var coordenadas = new Dictionary<string, (double lat, double lng)>(StringComparer.OrdinalIgnoreCase)
{
    ["PORTO"]               = (41.1579, -8.6291),
    ["BRAGA"]               = (41.5518, -8.4229),
    ["VIANA_DO_CASTELO"]    = (41.6939, -8.8317),
    ["GUIMARAES"]           = (41.4425, -8.2956),
    ["VILA_REAL"]           = (41.3006, -7.7457),
    ["BRAGANCA"]            = (41.8061, -6.7589),
    ["COIMBRA"]             = (40.2033, -8.4103),
    ["AVEIRO"]              = (40.6405, -8.6538),
    ["LEIRIA"]              = (39.7436, -8.8072),
    ["VISEU"]               = (40.6566, -7.9122),
    ["CASTELO_BRANCO"]      = (39.8229, -7.4912),
    ["GUARDA"]              = (40.5364, -7.2678),
    ["LISBOA"]              = (38.7169, -9.1399),
    ["SETUBAL"]             = (38.5244, -8.8882),
    ["EVORA"]               = (38.5714, -7.9081),
    ["BEJA"]                = (38.0150, -7.8641),
    ["FARO"]                = (37.0193, -7.9304),
    ["PORTIMAO"]            = (37.1387, -8.5381),
    ["PONTA_DELGADA"]       = (37.7412, -25.6756),
    ["ANGRA_DO_HEROISMO"]   = (38.6569, -27.2215),
    ["HORTA"]               = (38.5313, -28.6237),
    ["FUNCHAL"]             = (32.6483, -16.9050),
    ["PORTO_SANTO"]         = (33.0636, -16.3490),
    ["CAMARA_DE_LOBOS"]     = (32.6508, -17.0056),
};

// GET /sensores — lê da tabela sensores (com estado real)
app.MapGet("/sensores", () =>
{
    var lista = new List<object>();

    using var conn = new SqliteConnection(connectionString);
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT id, zona, estado, ultimo_contacto FROM sensores ORDER BY id";

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        string id    = reader.GetString(0);
        string zona  = reader.GetString(1);
        string estado = reader.GetString(2);

        coordenadas.TryGetValue(zona, out var coords);

        lista.Add(new
        {
            id,
            zona,
            estado,
            lat = coords.lat,
            lng = coords.lng
        });
    }

    return lista;
});

// POST /sensores/{id}/estado/{estado} — altera estado na BD e no CSV do Gateway
app.MapPost("/sensores/{id}/estado/{estado}", (string id, string estado) =>
{
    var estadosValidos = new[] { "ativo", "manutencao", "desativado" };
    if (!estadosValidos.Contains(estado))
        return Results.BadRequest(new { erro = $"Estado '{estado}' inválido." });

    // Atualiza SQLite
    using var conn = new SqliteConnection(connectionString);
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE sensores SET estado = @estado WHERE id = @id";
    cmd.Parameters.AddWithValue("@estado", estado);
    cmd.Parameters.AddWithValue("@id", id);

    int linhasAfetadas = cmd.ExecuteNonQuery();

    if (linhasAfetadas == 0)
        return Results.NotFound(new { erro = $"Sensor '{id}' não encontrado." });

    // Determina qual CSV atualizar com base no prefixo do sensor ID
    string csvFile = id.ToUpper() switch
    {
        var s when s.StartsWith("S_NRT") => "sensores_norte.csv",
        var s when s.StartsWith("S_CTR") => "sensores_centro.csv",
        var s when s.StartsWith("S_SUL") => "sensores_sul.csv",
        var s when s.StartsWith("S_ILH") => "sensores_ilhas.csv",
        _ => null
    };

    if (csvFile != null)
    {
        string csvPath = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "Gateway", "bin", "Debug", "net8.0", csvFile
        ));

        if (File.Exists(csvPath))
        {
            var linhas = File.ReadAllLines(csvPath).ToList();
            for (int i = 0; i < linhas.Count; i++)
            {
                var campos = linhas[i].Split(':', 5);
                if (campos[0].Trim().Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    linhas[i] = $"{campos[0]}:{estado}:{campos[2]}:{campos[3]}:{campos[4]}";
                    break;
                }
            }
            File.WriteAllLines(csvPath, linhas);
        }
    }

    // Registar histórico de estado
    var cmdHist = conn.CreateCommand();
    cmdHist.CommandText = "INSERT INTO historico_estado (sensor_id, estado, timestamp) VALUES (@hid, @estado, @ts)";
    cmdHist.Parameters.AddWithValue("@hid", id);
    cmdHist.Parameters.AddWithValue("@estado", estado);
    cmdHist.Parameters.AddWithValue("@ts", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
    try { cmdHist.ExecuteNonQuery(); } catch { /* tabela pode não existir em BD antiga */ }

    return Results.Ok(new { mensagem = $"Estado do sensor {id} atualizado para {estado}" });
});

// GET /medicoes — lê medições com filtros opcionais
app.MapGet("/medicoes", (string? sensor, string? tipo, string? zona, int limite = 200) =>
{
    var lista = new List<object>();

    using var conn = new SqliteConnection(connectionString);
    conn.Open();

    var cmd = conn.CreateCommand();
    var where = new List<string>();
    if (!string.IsNullOrWhiteSpace(sensor)) { where.Add("sensor_id = @sensor"); cmd.Parameters.AddWithValue("@sensor", sensor); }
    if (!string.IsNullOrWhiteSpace(tipo))   { where.Add("tipo = @tipo");         cmd.Parameters.AddWithValue("@tipo",   tipo);   }
    if (!string.IsNullOrWhiteSpace(zona))   { where.Add("zona = @zona");         cmd.Parameters.AddWithValue("@zona",   zona);   }

    string filtro = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
    cmd.CommandText = $"SELECT sensor_id, zona, tipo, valor, timestamp FROM medicoes {filtro} ORDER BY id DESC LIMIT @limite";
    cmd.Parameters.AddWithValue("@limite", Math.Clamp(limite, 1, 1000));

    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        lista.Add(new { sensor_id = reader.GetString(0), zona = reader.GetString(1), tipo = reader.GetString(2), valor = reader.GetDouble(3), timestamp = reader.GetString(4) });

    return lista;
});

// GET /medicoes/stats — contagens por tipo e zona
app.MapGet("/medicoes/stats", () =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();

    var porTipo = new List<object>();
    var cmd1 = conn.CreateCommand();
    cmd1.CommandText = "SELECT tipo, COUNT(*) FROM medicoes GROUP BY tipo ORDER BY COUNT(*) DESC";
    using (var r = cmd1.ExecuteReader())
        while (r.Read()) porTipo.Add(new { tipo = r.GetString(0), total = r.GetInt32(1) });

    var porZona = new List<object>();
    var cmd2 = conn.CreateCommand();
    cmd2.CommandText = "SELECT zona, COUNT(*) FROM medicoes GROUP BY zona ORDER BY COUNT(*) DESC";
    using (var r = cmd2.ExecuteReader())
        while (r.Read()) porZona.Add(new { zona = r.GetString(0), total = r.GetInt32(1) });

    var cmd3 = conn.CreateCommand();
    cmd3.CommandText = "SELECT COUNT(*) FROM medicoes";
    long totalGeral = (long)(cmd3.ExecuteScalar() ?? 0L);

    return new { totalGeral, porTipo, porZona };
});

// POST /sensores/{id}/remover — remove sensor da lista, mantém medições
app.MapPost("/sensores/{id}/remover", (string id) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM sensores WHERE id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    int linhas = cmd.ExecuteNonQuery();
    return linhas == 0
        ? Results.NotFound(new { erro = $"Sensor '{id}' não encontrado." })
        : Results.Ok(new { mensagem = $"Sensor '{id}' removido da lista (dados mantidos)." });
});

// POST /sensores/{id}/remover-tudo — remove sensor e todas as suas medições
app.MapPost("/sensores/{id}/remover-tudo", (string id) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var cmdMed = conn.CreateCommand();
    cmdMed.CommandText = "DELETE FROM medicoes WHERE sensor_id = @id";
    cmdMed.Parameters.AddWithValue("@id", id);
    int medicoes = cmdMed.ExecuteNonQuery();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM sensores WHERE id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    int linhas = cmd.ExecuteNonQuery();
    return linhas == 0
        ? Results.NotFound(new { erro = $"Sensor '{id}' não encontrado." })
        : Results.Ok(new { mensagem = $"Sensor '{id}' e {medicoes} medição(ões) removidos." });
});

// GET /alertas — últimos alertas de threshold
app.MapGet("/alertas", (int limite = 50) =>
{
    var lista = new List<object>();
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT sensor_id, zona, tipo, valor, timestamp FROM alertas ORDER BY id DESC LIMIT @limite";
    cmd.Parameters.AddWithValue("@limite", Math.Clamp(limite, 1, 500));
    try
    {
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            lista.Add(new { sensor_id = reader.GetString(0), zona = reader.GetString(1), tipo = reader.GetString(2), valor = reader.GetDouble(3), timestamp = reader.GetString(4) });
    }
    catch { /* tabela ainda não existe */ }
    return lista;
});

// GET /historico — histórico de mudanças de estado
app.MapGet("/historico", (string? sensor, int limite = 50) =>
{
    var lista = new List<object>();
    using var conn = new SqliteConnection(connectionString);
    conn.Open();
    var cmd = conn.CreateCommand();
    string where = string.IsNullOrWhiteSpace(sensor) ? "" : "WHERE sensor_id = @sensor";
    cmd.CommandText = $"SELECT sensor_id, estado, timestamp FROM historico_estado {where} ORDER BY id DESC LIMIT @limite";
    if (!string.IsNullOrWhiteSpace(sensor)) cmd.Parameters.AddWithValue("@sensor", sensor);
    cmd.Parameters.AddWithValue("@limite", Math.Clamp(limite, 1, 500));
    try
    {
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            lista.Add(new { sensor_id = reader.GetString(0), estado = reader.GetString(1), timestamp = reader.GetString(2) });
    }
    catch { /* tabela ainda não existe */ }
    return lista;
});

// GET / — teste rápido
app.MapGet("/", () => "API OneHealth a funcionar!");

// ── GESTÃO DE PROCESSOS ──────────────────────────────────────────────────────

string rootDir = Path.GetFullPath(Path.Combine(
    AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));

var processos = new Dictionary<string, Process>();
var processosLock = new object();

Process? IniciarProcesso(string csproj, string extraArgs = "")
{
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k dotnet run --project \"{csproj}\" -- {extraArgs}",
            UseShellExecute = true
        };
        return Process.Start(psi);
    }
    catch { return null; }
}

IResult PararProcesso(string chave)
{
    lock (processosLock)
    {
        if (!processos.TryGetValue(chave, out var p) || p.HasExited)
            return Results.BadRequest(new { erro = $"'{chave}' não está a correr." });
        try { p.Kill(true); } catch { }
        processos.Remove(chave);
        return Results.Ok(new { mensagem = $"'{chave}' parado." });
    }
}

// GET /sistema/status
app.MapGet("/sistema/status", () =>
{
    lock (processosLock)
    {
        return processos.Select(kv => new
        {
            nome  = kv.Key,
            ativo = !kv.Value.HasExited,
            pid   = kv.Value.HasExited ? (int?)null : kv.Value.Id
        }).ToList();
    }
});

// POST /sistema/servidor/start
app.MapPost("/sistema/servidor/start", () =>
{
    lock (processosLock)
    {
        if (processos.TryGetValue("servidor", out var p) && !p.HasExited)
            return Results.BadRequest(new { erro = "Servidor já está a correr." });

        var proc = IniciarProcesso(Path.Combine(rootDir, "Servidor", "Servidor.csproj"));
        if (proc is null) return Results.StatusCode(500);
        processos["servidor"] = proc;
        return Results.Ok(new { mensagem = "Servidor iniciado.", pid = proc.Id });
    }
});

// POST /sistema/servidor/stop
app.MapPost("/sistema/servidor/stop", () => PararProcesso("servidor"));

// POST /sistema/gateway/start/{zona}
app.MapPost("/sistema/gateway/start/{zona}", (string zona) =>
{
    var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["norte"] = "1", ["centro"] = "2", ["sul"] = "3", ["ilhas"] = "4" };

    if (!mapa.TryGetValue(zona, out string? num))
        return Results.BadRequest(new { erro = $"Zona inválida. Use: norte, centro, sul, ilhas." });

    string chave = $"gateway_{zona.ToLower()}";
    lock (processosLock)
    {
        if (processos.TryGetValue(chave, out var p) && !p.HasExited)
            return Results.BadRequest(new { erro = $"Gateway {zona} já está a correr." });

        var proc = IniciarProcesso(Path.Combine(rootDir, "Gateway", "Gateway.csproj"), $"--zona {num}");
        if (proc is null) return Results.StatusCode(500);
        processos[chave] = proc;
        return Results.Ok(new { mensagem = $"Gateway {zona} iniciada.", pid = proc.Id });
    }
});

// POST /sistema/gateway/stop/{zona}
app.MapPost("/sistema/gateway/stop/{zona}", (string zona) =>
    PararProcesso($"gateway_{zona.ToLower()}"));

// POST /sistema/sensor/start  body: { "zona": "1", "id": "S_NRT_001", "auto": true }
app.MapPost("/sistema/sensor/start", (SensorIniciarRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Id))
        return Results.BadRequest(new { erro = "Campo 'id' obrigatório." });
    if (string.IsNullOrWhiteSpace(req.Zona))
        return Results.BadRequest(new { erro = "Campo 'zona' obrigatório (1-4)." });

    string chave = $"sensor_{req.Id.ToLower()}";
    lock (processosLock)
    {
        if (processos.TryGetValue(chave, out var p) && !p.HasExited)
            return Results.BadRequest(new { erro = $"Sensor {req.Id} já está a correr." });

        string extraArgs = $"--zona {req.Zona} --id {req.Id}" + (req.Auto ? " --auto" : "");
        var proc = IniciarProcesso(Path.Combine(rootDir, "Sensor", "Sensor.csproj"), extraArgs);
        if (proc is null) return Results.StatusCode(500);
        processos[chave] = proc;
        return Results.Ok(new { mensagem = $"Sensor {req.Id} iniciado.", pid = proc.Id });
    }
});

// POST /sistema/sensor/stop/{id}
app.MapPost("/sistema/sensor/stop/{id}", (string id) =>
    PararProcesso($"sensor_{id.ToLower()}"));

app.Run();

record SensorIniciarRequest(string Zona, string Id, bool Auto = false);
