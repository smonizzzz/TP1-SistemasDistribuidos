using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// ✅ CORS (permite o HTML aceder)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("AllowAll");

// 📍 Caminho da base de dados
string dbPath = @"C:\Users\Moniz\Desktop\TP1-SistemasDistribuidos-master\Servidor\bin\Debug\net8.0\onehealth.db";
string connectionString = $"Data Source={dbPath}";


// 🔥 ENDPOINT GET SENSORES
app.MapGet("/sensores", () =>
{
    var sensores = new Dictionary<string, dynamic>();

    using var conn = new SqliteConnection(connectionString);
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT sensor_id, zona
        FROM medicoes
        ORDER BY timestamp DESC
    ";

    using var reader = cmd.ExecuteReader();

    while (reader.Read())
    {
        string id = reader.GetString(0);
        string zona = reader.GetString(1);

        if (!sensores.ContainsKey(id))
        {
            sensores[id] = new
            {
                id = id,
                zona = zona,
                estado = "ativo", // 🔥 depois vamos tornar real

                lat = 38.7 + new Random().NextDouble(),
                lng = -9.2 + new Random().NextDouble()
            };
        }
    }

    return sensores.Values;
});


// 🔥 ENDPOINT ALTERAR ESTADO
app.MapPost("/sensores/{id}/estado/{estado}", (string id, string estado) =>
{
    using var conn = new SqliteConnection(connectionString);
    conn.Open();

    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        UPDATE sensores
        SET estado = @estado
        WHERE id = @id;
    ";

    cmd.Parameters.AddWithValue("@id", id);
    cmd.Parameters.AddWithValue("@estado", estado);

    cmd.ExecuteNonQuery();

    return Results.Ok(new { mensagem = $"Estado do sensor {id} atualizado para {estado}" });
});


// 🔹 teste simples
app.MapGet("/", () => "API OneHealth a funcionar!");

app.Run();