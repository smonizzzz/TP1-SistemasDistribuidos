using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;

class Servidor
{
    static string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "onehealth.db");
    static string connectionString = $"Data Source={dbPath}";

    static readonly object lockStats = new object();
    static int totalMedicoes = 0;
    static Dictionary<string, int> statsPerTipo = new Dictionary<string, int>();
    static Dictionary<string, int> statsPerZona = new Dictionary<string, int>();

    static void Main()
    {
        CriarBaseDeDados();

        TcpListener server = new TcpListener(IPAddress.Any, 9000);
        server.Start();

        Console.WriteLine("================================================");
        Console.WriteLine(">>> SERVIDOR ONE HEALTH - ATIVO");
        Console.WriteLine(">>> Base de dados SQLite pronta (FK ativas)");
        Console.WriteLine($">>> Ficheiro DB: {dbPath}");
        Console.WriteLine(">>> A aguardar dados da Gateway na porta 9000...");
        Console.WriteLine("================================================");

        while (true)
        {
            try
            {
                TcpClient gatewayClient = server.AcceptTcpClient();

                new Thread(() =>
                {
                    ProcessarGateway(gatewayClient);
                }) { IsBackground = true }.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] {ex.Message}");
            }
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
    }

    static void RegistarSensor(string sensorId, string zona)
    {
        using var connection = AbrirConexao();

        string sql = @"
            INSERT INTO sensores (id, zona, ultimo_contacto)
            VALUES (@id, @zona, @ultimo_contacto)
            ON CONFLICT(id) DO UPDATE SET
                zona = excluded.zona,
                ultimo_contacto = excluded.ultimo_contacto;
        ";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", sensorId);
        cmd.Parameters.AddWithValue("@zona", zona);
        cmd.Parameters.AddWithValue("@ultimo_contacto", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"));
        cmd.ExecuteNonQuery();
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
}
