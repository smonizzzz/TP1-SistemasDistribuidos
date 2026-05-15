using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Grpc.Net.Client;
using ServicoAnalise;

class Servidor
{
    static string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "onehealth.db");
    static string connectionString = $"Data Source={dbPath}";

    static readonly object lockStats = new object();
    static int totalMedicoes = 0;
    static Dictionary<string, int> statsPerTipo = new Dictionary<string, int>();
    static Dictionary<string, int> statsPerZona = new Dictionary<string, int>();

    // Canal gRPC para o serviço de análise Python (reutilizável)
    static GrpcChannel? analiseChannel;
    static AnaliseService.AnaliseServiceClient? analiseClient;

    static void Main()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        analiseChannel = GrpcChannel.ForAddress("http://localhost:5200");
        analiseClient  = new AnaliseService.AnaliseServiceClient(analiseChannel);

        CriarBaseDeDados();

        // Porta 9000 — Gateway → Servidor (dados)
        TcpListener server = new TcpListener(IPAddress.Any, 9000);
        server.Start();

        // Porta 9001 — API → Servidor (pedidos de análise RPC)
        TcpListener serverAnalise = new TcpListener(IPAddress.Any, 9001);
        serverAnalise.Start();

        Console.WriteLine("<================================================>");
        Console.WriteLine(">>> SERVIDOR ONE HEALTH - ATIVO");
        Console.WriteLine(">>> Base de dados SQLite pronta");
        Console.WriteLine($">>> Ficheiro DB: {dbPath}");
        Console.WriteLine($">>> Iniciado em: {DateTime.Now}");
        Console.WriteLine(">>> Gateway → Servidor: porta 9000");
        Console.WriteLine(">>> API → Análise:      porta 9001");
        Console.WriteLine("<================================================>");

        // Thread para porta 9001 (análise)
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var apiClient = serverAnalise.AcceptTcpClient();
                    new Thread(() => ProcessarPedidoAnalise(apiClient)) { IsBackground = true }.Start();
                }
                catch (Exception ex) { Console.WriteLine($"[ERRO ANÁLISE] {ex.Message}"); }
            }
        }) { IsBackground = true }.Start();

        while (true)
        {
            try
            {
                TcpClient gatewayClient = server.AcceptTcpClient();
                new Thread(() => ProcessarGateway(gatewayClient)) { IsBackground = true }.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] {ex.Message}");
            }
        }
    }

    static void ProcessarPedidoAnalise(TcpClient client)
    {
        try
        {
            using (client)
            {
                using var reader = new StreamReader(client.GetStream());
                using var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };

                string? linha = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(linha)) return;

                // Formato: ANALISAR <tipo> <zona> <sensor_id> <inicio> <fim>
                string[] p = linha.Split(' ');
                if (p[0] != "ANALISAR" || p.Length < 6)
                {
                    writer.WriteLine("ERRO formato_invalido");
                    return;
                }

                string tipo     = p[1] == "-" ? "" : p[1];
                string zona     = p[2] == "-" ? "" : p[2];
                string sensorId = p[3] == "-" ? "" : p[3];
                string inicio   = p[4] == "-" ? "" : p[4];
                string fim      = p[5] == "-" ? "" : p[5];

                Console.WriteLine($"[ANÁLISE] Pedido recebido: tipo={tipo} zona={zona} sensor={sensorId}");

                var resultado = InvocarServicoAnalise(tipo, zona, sensorId, inicio, fim);
                writer.WriteLine(resultado);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO ANÁLISE] {ex.Message}");
        }
    }

    static string InvocarServicoAnalise(string tipo, string zona, string sensorId, string inicio, string fim)
    {
        try
        {
            var resposta = analiseClient!.AnalisarDados(new PedidoAnalise
            {
                Tipo     = tipo,
                Zona     = zona,
                SensorId = sensorId,
                Inicio   = inicio,
                Fim      = fim
            });

            Console.WriteLine($"[ANÁLISE RPC] media={resposta.Media} risco={resposta.NivelRisco} n={resposta.TotalMedicoes}");

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                media         = resposta.Media,
                maximo        = resposta.Maximo,
                minimo        = resposta.Minimo,
                desvio_padrao = resposta.DesvioPadrao,
                nivel_risco   = resposta.NivelRisco,
                tendencia     = resposta.Tendencia,
                total_medicoes = resposta.TotalMedicoes,
                resumo        = resposta.Resumo
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ANÁLISE RPC] Serviço indisponível: {ex.Message}");
            return System.Text.Json.JsonSerializer.Serialize(new { erro = "Serviço de análise indisponível." });
        }
    }

    static void ProcessarGateway(TcpClient gatewayClient)
    {
        try
        {
            using (gatewayClient)
            using (StreamReader reader = new StreamReader(gatewayClient.GetStream()))
            using (StreamWriter writer = new StreamWriter(gatewayClient.GetStream()) { AutoFlush = true })
            {
                string? message;
                while ((message = reader.ReadLine()) != null)
                {
                    Console.WriteLine($"[RECEBIDO] {message}");

                    string[] p = message.Split(' ');

                    if (p[0] == "SENSOR_CONNECT" && p.Length >= 3)
                    {
                        string sensorId = p[1];
                        string zona = p[2];

                        RegistarSensor(sensorId, zona);
                        Console.WriteLine($"[SENSOR] {sensorId} registado/atualizado na zona {zona}");
                    }
                    else if (p[0] == "FORWARD_DATA" && p.Length >= 6)
                    {
                        string sensorId = p[1];
                        string zona = p[2];
                        string tipo = p[3];
                        string valorTexto = p[4];
                        string timestamp = p[5];

                        if (double.TryParse(valorTexto, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double valor))
                        {
                            GuardarMedicao(sensorId, zona, tipo, valor, timestamp);

                            int total, countTipo, countZona;
                            lock (lockStats)
                            {
                                totalMedicoes++;
                                statsPerTipo.TryGetValue(tipo, out countTipo);
                                statsPerTipo[tipo] = countTipo + 1;
                                statsPerZona.TryGetValue(zona, out countZona);
                                statsPerZona[zona] = countZona + 1;
                                total = totalMedicoes;
                            }

                            Console.WriteLine($"[BD] Medição guardada -> Sensor:{sensorId} | Zona:{zona} | Tipo:{tipo} | Valor:{valor}");
                            Console.WriteLine($"[STATS] Total:{total} | Por tipo: {string.Join(", ", statsPerTipo.Select(kv => $"{kv.Key}={kv.Value}"))} | Por zona: {string.Join(", ", statsPerZona.Select(kv => $"{kv.Key}={kv.Value}"))}");
                        }
                        else
                        {
                            Console.WriteLine($"[ERRO] Valor inválido recebido: {valorTexto}");
                        }
                    }
                    else if (p[0] == "SENSOR_DISCONNECT" && p.Length >= 2)
                    {
                        string sensorId = p[1];
                        AtualizarUltimoContacto(sensorId);
                        Console.WriteLine($"[SENSOR] {sensorId} desligado corretamente.");
                    }
                    else if (p[0] == "ALERT" && p.Length >= 6)
                    {
                        GuardarAlerta(p[1], p[2], p[3], p[4], p[5]);
                        Console.WriteLine($"[⚠ ALERTA] Sensor:{p[1]} | {p[3]}={p[4]} excede o limite!");
                    }
                    else if (p[0] == "SENSOR_ESTADO_CHANGE" && p.Length >= 3)
                    {
                        AtualizarEstadoBD(p[1], p[2]);
                        RegistarHistoricoEstado(p[1], p[2]);
                        Console.WriteLine($"[ESTADO] Sensor {p[1]} → {p[2]}");
                    }

                    writer.WriteLine("ACK_OK");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO] Conexão com Gateway interrompida: {ex.Message}");
        }
    }

    static SqliteConnection AbrirConexao()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        new SqliteCommand("PRAGMA foreign_keys = ON", conn).ExecuteNonQuery();
        return conn;
    }

    static void CriarBaseDeDados()
    {
        using var connection = AbrirConexao();

        string sqlSensores = @"
            CREATE TABLE IF NOT EXISTS sensores (
                id TEXT PRIMARY KEY,
                zona TEXT NOT NULL,
                estado TEXT NOT NULL DEFAULT 'ativo',
                ultimo_contacto TEXT NOT NULL
            );
        ";

        string sqlMedicoes = @"
            CREATE TABLE IF NOT EXISTS medicoes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sensor_id TEXT NOT NULL,
                zona TEXT NOT NULL,
                tipo TEXT NOT NULL,
                valor REAL NOT NULL,
                timestamp TEXT NOT NULL,
                FOREIGN KEY(sensor_id) REFERENCES sensores(id)
            );
        ";

        using var cmd1 = new SqliteCommand(sqlSensores, connection);
        cmd1.ExecuteNonQuery();

        using var cmd2 = new SqliteCommand(sqlMedicoes, connection);
        cmd2.ExecuteNonQuery();

        string sqlAlertas = @"
            CREATE TABLE IF NOT EXISTS alertas (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sensor_id TEXT NOT NULL,
                zona TEXT NOT NULL,
                tipo TEXT NOT NULL,
                valor REAL NOT NULL,
                timestamp TEXT NOT NULL
            );
        ";

        string sqlHistorico = @"
            CREATE TABLE IF NOT EXISTS historico_estado (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                sensor_id TEXT NOT NULL,
                estado TEXT NOT NULL,
                timestamp TEXT NOT NULL
            );
        ";

        using var cmd3 = new SqliteCommand(sqlAlertas, connection);
        cmd3.ExecuteNonQuery();

        using var cmd4 = new SqliteCommand(sqlHistorico, connection);
        cmd4.ExecuteNonQuery();

        // Migração: adiciona coluna estado a DBs existentes sem ela
        try
        {
            using var cmdMigrate = new SqliteCommand(
                "ALTER TABLE sensores ADD COLUMN estado TEXT NOT NULL DEFAULT 'ativo'", connection);
            cmdMigrate.ExecuteNonQuery();
        }
        catch { /* coluna já existe — ignorar */ }
    }

    static void RegistarSensor(string sensorId, string zona)
    {
        using var connection = AbrirConexao();

        string sql = @"
            INSERT INTO sensores (id, zona, estado, ultimo_contacto)
            VALUES (@id, @zona, 'ativo', @ultimo_contacto)
            ON CONFLICT(id) DO UPDATE SET
                zona = excluded.zona,
                estado = 'ativo',
                ultimo_contacto = excluded.ultimo_contacto;
        ";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", sensorId);
        cmd.Parameters.AddWithValue("@zona", zona);
        cmd.Parameters.AddWithValue("@ultimo_contacto", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
        cmd.ExecuteNonQuery();

        RegistarHistoricoEstado(sensorId, "ativo");
    }

    static void AtualizarUltimoContacto(string sensorId)
    {
        using var connection = AbrirConexao();

        string sql = @"
            UPDATE sensores SET ultimo_contacto = @ultimo_contacto WHERE id = @id;
        ";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", sensorId);
        cmd.Parameters.AddWithValue("@ultimo_contacto", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    static void GuardarMedicao(string sensorId, string zona, string tipo, double valor, string timestamp)
    {
        using var connection = AbrirConexao();

        string sql = @"
            INSERT INTO medicoes (sensor_id, zona, tipo, valor, timestamp)
            VALUES (@sensor_id, @zona, @tipo, @valor, @timestamp);
        ";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@sensor_id", sensorId);
        cmd.Parameters.AddWithValue("@zona", zona);
        cmd.Parameters.AddWithValue("@tipo", tipo);
        cmd.Parameters.AddWithValue("@valor", valor);
        cmd.Parameters.AddWithValue("@timestamp", timestamp);
        cmd.ExecuteNonQuery();
    }

    static void GuardarAlerta(string sensorId, string zona, string tipo, string valorStr, string timestamp)
    {
        if (!double.TryParse(valorStr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double valor)) return;
        using var conn = AbrirConexao();
        var cmd = new SqliteCommand(
            "INSERT INTO alertas (sensor_id, zona, tipo, valor, timestamp) VALUES (@id, @zona, @tipo, @valor, @ts)", conn);
        cmd.Parameters.AddWithValue("@id", sensorId);
        cmd.Parameters.AddWithValue("@zona", zona);
        cmd.Parameters.AddWithValue("@tipo", tipo);
        cmd.Parameters.AddWithValue("@valor", valor);
        cmd.Parameters.AddWithValue("@ts", timestamp);
        cmd.ExecuteNonQuery();
    }

    static void AtualizarEstadoBD(string sensorId, string estado)
    {
        using var conn = AbrirConexao();
        var cmd = new SqliteCommand("UPDATE sensores SET estado = @estado WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@estado", estado);
        cmd.Parameters.AddWithValue("@id", sensorId);
        cmd.ExecuteNonQuery();
    }

    static void RegistarHistoricoEstado(string sensorId, string estado)
    {
        using var conn = AbrirConexao();
        var cmd = new SqliteCommand(
            "INSERT INTO historico_estado (sensor_id, estado, timestamp) VALUES (@id, @estado, @ts)", conn);
        cmd.Parameters.AddWithValue("@id", sensorId);
        cmd.Parameters.AddWithValue("@estado", estado);
        cmd.Parameters.AddWithValue("@ts", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
        cmd.ExecuteNonQuery();
    }
}
