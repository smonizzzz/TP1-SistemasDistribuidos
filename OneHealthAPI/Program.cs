using Microsoft.Data.Sqlite;
using System.Linq;

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
                var campos = linhas[i].Split(':');
                if (campos[0].Trim().Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    linhas[i] = $"{campos[0]}:{estado}:{campos[2]}:{campos[3]}:{campos[4]}";
                    break;
                }
            }
            File.WriteAllLines(csvPath, linhas);
        }
    }

    return Results.Ok(new { mensagem = $"Estado do sensor {id} atualizado para {estado}" });
});

// GET / — teste rápido
app.MapGet("/", () => "API OneHealth a funcionar!");

app.Run();
