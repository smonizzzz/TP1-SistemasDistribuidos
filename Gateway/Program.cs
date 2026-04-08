using System;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Collections.Generic;
using System.Linq;

class Gateway
{
    static string csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sensores.csv");

    static void Main()
    {
        TcpListener listener = new TcpListener(IPAddress.Any, 8000);
        listener.Start();

        new Thread(() =>
        {
            while (true)
            {
                VerificarSensoresOffline();
                Thread.Sleep(5000);
            }
        }).Start();

        Console.WriteLine("================================================");
        Console.WriteLine(">>> GATEWAY PROFISSIONAL ATIVA (Porta 8000)");
        Console.WriteLine($">>> FICHEIRO: {csvPath}");
        Console.WriteLine("================================================");

        if (!File.Exists(csvPath))
        {
            Console.WriteLine("[AVISO] sensores.csv não encontrado! A criar ficheiro de exemplo...");
            File.WriteAllText(csvPath, "S101:ativo:ZONA_CENTRO:[TEMP,HUM]:2026-03-10T08:45:00\nS102:manutencao:ZONA_ESCOLAR:[PM2.5]:2026-03-10T09:00:00");
        }

        while (true)
        {
            try
            {
                TcpClient sensorClient = listener.AcceptTcpClient();

                new Thread(() =>
                {
                    ProcessarComunicacao(sensorClient);
                }).Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO CRÍTICO] Falha no loop principal: {ex.Message}");
            }
        }
    }

    static void ProcessarComunicacao(TcpClient sensorClient)
    {
        try
        {
            using (StreamReader reader = new StreamReader(sensorClient.GetStream()))
            using (StreamWriter writer = new StreamWriter(sensorClient.GetStream()) { AutoFlush = true })
            {
                TcpClient serverClient = new TcpClient("127.0.0.1", 9000);
                StreamWriter serverWriter = new StreamWriter(serverClient.GetStream()) { AutoFlush = true };
                StreamReader serverReader = new StreamReader(serverClient.GetStream());

                string sensorId = "";
                string zonaAtribuida = "";
                string message;

                while ((message = reader.ReadLine()) != null)
                {
                    Console.WriteLine($"[RECEBIDO] {message}");
                    string[] parts = message.Split(' ');
                    string comando = parts[0].ToUpper();

                    // --- CONNECT ---
                    if (comando == "CONNECT" && parts.Length >= 2)
                    {
                        string id = parts[1];
                        var info = ObterInfoSensor(id);

                        if (info != null && info[1] == "ativo")
                        {
                            sensorId = id;
                            zonaAtribuida = info[2];
                            writer.WriteLine("ACK_OK");

                            AtualizarLastSync(sensorId);

                            serverWriter.WriteLine($"SENSOR_CONNECT {sensorId} {zonaAtribuida}");
                            serverReader.ReadLine();

                            Console.WriteLine($"[SISTEMA] Sensor {id} autorizado na {zonaAtribuida}.");
                        }
                        else
                        {
                            writer.WriteLine("ACK_ERR_UNAUTHORIZED");
                            Console.WriteLine($"[NEGADO] Sensor {id} inválido ou em manutenção.");
                            break;
                        }
                    }

                    // --- TYPES ---
                    else if (comando == "TYPES" && !string.IsNullOrEmpty(sensorId))
                    {
                        writer.WriteLine("ACK_OK");
                        Console.WriteLine($"[INFO] Tipos recebidos do sensor {sensorId}: {string.Join(" ", parts.Skip(1))}");
                    }

                    // --- HEARTBEAT ---
                    else if (comando == "HEARTBEAT" && !string.IsNullOrEmpty(sensorId))
                    {
                        writer.WriteLine("ACK_OK");
                        AtualizarLastSync(sensorId);
                        Console.WriteLine($"[HEARTBEAT] Sensor {sensorId} ativo.");
                    }

                    // --- DATA ---
                    else if (comando == "DATA" && parts.Length >= 4 && !string.IsNullOrEmpty(sensorId))
                    {
                        string tipo = parts[1].Trim();
                        var info = ObterInfoSensor(sensorId);

                        string tiposPermitidos = info[3]
                           .Replace("[", "")
                           .Replace("]", "");

                        string[] listaTipos = tiposPermitidos.Split(',');

                        // remover espaços extra
                        listaTipos = listaTipos.Select(t => t.Trim()).ToArray();

                        if (info != null && listaTipos.Contains(tipo))
                        {
                            writer.WriteLine("ACK_OK");
                            AtualizarLastSync(sensorId);

                            serverWriter.WriteLine($"FORWARD_DATA {sensorId} {zonaAtribuida} {tipo} {parts[2]} {parts[3]}");
                            serverReader.ReadLine();

                            Console.WriteLine($"[DADOS] {tipo} enviado para o servidor.");
                        }
                        else
                        {
                            writer.WriteLine("ACK_ERR_INVALID_TYPE");
                            Console.WriteLine($"[ERRO] Tipo {tipo} não permitido para o sensor {sensorId}.");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO] Conexão terminada: {ex.Message}");
        }
    }

    static string[] ObterInfoSensor(string id)
    {
        try
        {
            string[] linhas = File.ReadAllLines(csvPath);
            foreach (string linha in linhas)
            {
                string[] campos = linha.Split(':');
                if (campos[0].Trim().Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                    return campos;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO LEITURA] {ex.Message}");
        }
        return null;
    }

    static void AtualizarLastSync(string id)
    {
        try
        {
            List<string> linhas = File.ReadAllLines(csvPath).ToList();
            bool alterado = false;

            for (int i = 0; i < linhas.Count; i++)
            {
                string[] campos = linhas[i].Split(':');
                if (campos[0].Trim().Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    linhas[i] = $"{campos[0]}:{campos[1]}:{campos[2]}:{campos[3]}:{DateTime.Now:yyyy-MM-ddTHH:mm:ss}";
                    alterado = true;
                    break;
                }
            }

            if (alterado)
            {
                File.WriteAllLines(csvPath, linhas);
                Console.WriteLine($"[ARQUIVO] Last Sync atualizado para {id}.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO ESCRITA] Não foi possível atualizar o CSV: {ex.Message}");
        }
    }
    static void VerificarSensoresOffline()
    {
        try
        {
            string[] linhas = File.ReadAllLines(csvPath);

            foreach (string linha in linhas)
            {
                string[] campos = linha.Split(':');

                // Segurança extra (evita erros se linha estiver mal)
                if (campos.Length < 5)
                    continue;

                string id = campos[0];
                string estado = campos[1];
                string lastSyncStr = campos[4];

                // Tentar converter data
                if (!DateTime.TryParse(lastSyncStr, out DateTime lastSync))
                {
                    continue; // ignora linhas inválidas
                }

                double diferenca = (DateTime.Now - lastSync).TotalSeconds;

                // 🔍 DEBUG (podes remover depois)
                Console.WriteLine($"[DEBUG] Sensor {id} - Último sync: {lastSync} - Dif: {diferenca:F1}s");

                // Verifica se está offline
                if (diferenca > 10 && estado == "ativo")
                {
                    Console.WriteLine($"[ALERTA] Sensor {id} está OFFLINE!");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO OFFLINE] {ex.Message}");
        }
    }
}