using System;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Grpc.Net.Client;
using PreProcessamento;

class Gateway
{
    static string csvPath = "";
    static readonly object lockCsv = new object();
    static readonly DateTime gatewayStartTime = DateTime.Now;

    // Canal gRPC reutilizável (criado uma vez, partilhado por todas as threads)
    static GrpcChannel? rpcChannel;
    static PreProcessamentoService.PreProcessamentoServiceClient? rpcClient;

    static readonly Dictionary<string, double> thresholds = new()
    {
        ["TEMP"]  = 40.0,
        ["CO2"]   = 800.0,
        ["PM25"]  = 75.0,
        ["PM10"]  = 150.0,
        ["RUIDO"] = 85.0
    };

    static void Main(string[] args)
    {
        string opcao = "";

        int zonaIdx = Array.IndexOf(args, "--zona");
        if (zonaIdx >= 0 && zonaIdx + 1 < args.Length)
        {
            opcao = args[zonaIdx + 1];
        }
        else
        {
            Console.WriteLine("<========================================>");
            Console.WriteLine("      SISTEMA DE MONITORIZAÇÃO");
            Console.WriteLine("<========================================>");
            Console.WriteLine("Escolhe a zona da Gateway:");
            Console.WriteLine("  1. Norte  (porta 8001)");
            Console.WriteLine("  2. Centro (porta 8002)");
            Console.WriteLine("  3. Sul    (porta 8003)");
            Console.WriteLine("  4. Ilhas  (porta 8004)");
            Console.Write("Opção: ");
            opcao = Console.ReadLine() ?? "";
        }
        
        string nomeZona;
        int porta;
        string csvFile;

        switch (opcao)
        {
            case "1": nomeZona = "NORTE";  porta = 8001; csvFile = "sensores_norte.csv";  break;
            case "2": nomeZona = "CENTRO"; porta = 8002; csvFile = "sensores_centro.csv"; break;
            case "3": nomeZona = "SUL";    porta = 8003; csvFile = "sensores_sul.csv";    break;
            case "4": nomeZona = "ILHAS";  porta = 8004; csvFile = "sensores_ilhas.csv";  break;
            default:
                Console.WriteLine("Opção inválida. A terminar.");
                return;
        }

        csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, csvFile);

        if (!File.Exists(csvPath))
        {
            Console.WriteLine($"[ERRO] Ficheiro '{csvFile}' não encontrado.");
            return;
        }

        // Inicializar canal gRPC uma única vez
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        rpcChannel = GrpcChannel.ForAddress("http://localhost:5100");
        rpcClient  = new PreProcessamentoService.PreProcessamentoServiceClient(rpcChannel);

        TcpListener listener = new TcpListener(IPAddress.Any, porta);
        listener.Start();

        // Reinicia last_sync de todos os sensores ativos para agora,
        // evitando que o monitor os marque imediatamente como manutencao
        ReiniciarLastSync();

        // Thread de monitorização offline (background)
        new Thread(() =>
        {
            while (true)
            {
                VerificarSensoresOffline();
                Thread.Sleep(5000);
            }
        }) { IsBackground = true }.Start();

        Console.WriteLine("================================================");
        Console.WriteLine($">>> GATEWAY {nomeZona} ATIVA (Porta {porta})");
        Console.WriteLine($">>> FICHEIRO: {csvFile}");
        Console.WriteLine("================================================");

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
        // Conexão ao servidor fechada corretamente com using
        TcpClient? serverClient = null;
        StreamWriter? serverWriter = null;
        StreamReader? serverReader = null;
        string sensorId = "";
        string zonaAtribuida = "";

        try
        {
            using StreamReader reader = new StreamReader(sensorClient.GetStream());
            using StreamWriter writer = new StreamWriter(sensorClient.GetStream()) { AutoFlush = true };

            try
            {
                serverClient = new TcpClient("127.0.0.1", 9000);
                serverWriter = new StreamWriter(serverClient.GetStream()) { AutoFlush = true };
                serverReader = new StreamReader(serverClient.GetStream());
            }
            catch (Exception)
            {
                Console.WriteLine("[ERRO] Servidor indisponível. A rejeitar ligação do sensor.");
                writer.WriteLine("ACK_ERR servidor_indisponivel");
                return;
            }
            List<string>? tiposRegistados = null;
            bool videoStreaming = false;
            int frameCount = 0;
            string? message;

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

                        Console.WriteLine($"[SISTEMA] Sensor {id} autorizado na zona {zonaAtribuida}.");
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
                    string tiposStr = string.Join(" ", parts.Skip(1));
                    tiposRegistados = [.. tiposStr.Replace("[", "").Replace("]", "")
                        .Split(',').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t))];
                    writer.WriteLine("ACK_OK");
                    Console.WriteLine($"[INFO] Tipos registados pelo sensor {sensorId}: {string.Join(", ", tiposRegistados)}");
                }

                // --- HEARTBEAT ---
                else if (comando == "HEARTBEAT" && !string.IsNullOrEmpty(sensorId))
                {
                    var infoHb = ObterInfoSensor(sensorId);
                    if (infoHb != null && infoHb[1] == "ativo")
                    {
                        writer.WriteLine("ACK_OK");
                        AtualizarLastSync(sensorId);
                        Console.WriteLine($"[HEARTBEAT] Sensor {sensorId} ativo.");
                    }
                    else
                    {
                        writer.WriteLine("ACK_ERR_UNAUTHORIZED");
                        Console.WriteLine($"[NEGADO] Sensor {sensorId} desativado remotamente. A encerrar sessão.");
                        break;
                    }
                }

                // --- DATA ---
                else if (comando == "DATA" && parts.Length >= 4 && !string.IsNullOrEmpty(sensorId))
                {
                    string tipo = parts[1].Trim();
                    var info = ObterInfoSensor(sensorId);

                    if (info != null && info[1] == "ativo")
                    {
                        string[] listaTipos = tiposRegistados != null
                            ? [.. tiposRegistados]
                            : [.. info[3].Replace("[", "").Replace("]", "")
                                .Split(',').Select(t => t.Trim())];

                        if (listaTipos.Contains(tipo))
                        {
                            writer.WriteLine("ACK_OK");
                            AtualizarLastSync(sensorId);

                            // --- RPC: Pré-processamento dos dados ---
                            string valorFinal = parts[2];
                            string timestampFinal = parts[3];
                            bool dadoValido = true;

                            try
                            {
                                var resposta = rpcClient!.ProcessarDados(new DadosBrutos
                                {
                                    SensorId  = sensorId,
                                    Zona      = zonaAtribuida,
                                    Tipo      = tipo,
                                    Valor     = double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0,
                                    Timestamp = parts[3]
                                });

                                valorFinal = resposta.Valor.ToString(CultureInfo.InvariantCulture);
                                timestampFinal = resposta.Timestamp;
                                dadoValido = resposta.Valido;

                                Console.WriteLine($"[RPC] Pré-processamento: {tipo}={valorFinal} | Válido={dadoValido} | {resposta.Mensagem}");
                            }
                            catch (Exception rpcEx)
                            {
                                Console.WriteLine($"[RPC] Serviço indisponível — a usar dados originais. ({rpcEx.Message})");
                            }

                            if (dadoValido)
                            {
                                serverWriter.WriteLine($"FORWARD_DATA {sensorId} {zonaAtribuida} {tipo} {valorFinal} {timestampFinal}");
                                serverReader.ReadLine();
                                Console.WriteLine($"[DADOS] {tipo}={valorFinal} enviado para o servidor.");

                                if (thresholds.TryGetValue(tipo, out double limite) &&
                                    double.TryParse(valorFinal, NumberStyles.Any, CultureInfo.InvariantCulture, out double valNum) &&
                                    valNum > limite)
                                {
                                    serverWriter.WriteLine($"ALERT {sensorId} {zonaAtribuida} {tipo} {valorFinal} {timestampFinal}");
                                    serverReader.ReadLine();
                                    Console.WriteLine($"[⚠ ALERTA] Sensor {sensorId}: {tipo}={valNum} excede o limite de {limite}!");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[RPC] Dado inválido descartado: {tipo}={valorFinal}");
                            }
                        }
                        else
                        {
                            writer.WriteLine("ACK_ERR_INVALID_TYPE");
                            Console.WriteLine($"[ERRO] Tipo {tipo} não permitido para o sensor {sensorId}.");
                        }
                    }
                    else
                    {
                        writer.WriteLine("ACK_ERR_UNAUTHORIZED");
                        Console.WriteLine($"[NEGADO] Sensor {sensorId} desativado remotamente. A encerrar sessão.");
                        break;
                    }
                }

                // --- VIDEO_STREAM_START ---
                else if (comando == "VIDEO_STREAM_START" && !string.IsNullOrEmpty(sensorId))
                {
                    videoStreaming = true;
                    frameCount = 0;
                    writer.WriteLine("ACK_OK");
                    Console.WriteLine($"[VIDEO] Stream iniciada pelo sensor {sensorId}.");
                }

                // --- FRAME ---
                else if (comando == "FRAME" && videoStreaming)
                {
                    frameCount++;
                    Console.WriteLine($"[VIDEO] Edge processing: frame #{frameCount} do sensor {sensorId}.");
                }

                // --- VIDEO_STREAM_END ---
                else if (comando == "VIDEO_STREAM_END" && !string.IsNullOrEmpty(sensorId))
                {
                    videoStreaming = false;
                    writer.WriteLine("ACK_OK");
                    Console.WriteLine($"[VIDEO] Stream encerrada pelo sensor {sensorId}. Total de frames: {frameCount}.");
                    frameCount = 0;
                }

                // --- DISCONNECT ---
                else if (comando == "DISCONNECT")
                {
                    writer.WriteLine("ACK_BYE");

                    if (!string.IsNullOrEmpty(sensorId))
                    {
                        serverWriter.WriteLine($"SENSOR_DISCONNECT {sensorId}");
                        serverReader.ReadLine();
                    }

                    Console.WriteLine($"[SISTEMA] Sensor {sensorId} desligou-se corretamente.");
                    break;
                }
            }
        }
        catch (Exception)
        {
            if (!string.IsNullOrEmpty(sensorId))
            {
                var infoFinal = ObterInfoSensor(sensorId);
                if (infoFinal != null && infoFinal[1] == "manutencao")
                    Console.WriteLine($"[SISTEMA] Sensor {sensorId} em manutenção — ligação encerrada pelo administrador.");
                else
                    Console.WriteLine($"[SISTEMA] Sensor {sensorId} desligou-se inesperadamente.");
            }
        }
        finally
        {
            serverReader?.Close();
            serverWriter?.Close();
            serverClient?.Close();
            sensorClient.Close();
        }
    }

    static string[]? ObterInfoSensor(string id)
    {
        try
        {
            lock (lockCsv)
            {
                string[] linhas = File.ReadAllLines(csvPath);
                foreach (string linha in linhas)
                {
                    string[] campos = linha.Split(':', 5);
                    if (campos[0].Trim().Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                        return campos;
                }
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
            lock (lockCsv)
            {
                List<string> linhas = File.ReadAllLines(csvPath).ToList();
                bool alterado = false;

                for (int i = 0; i < linhas.Count; i++)
                {
                    string[] campos = linhas[i].Split(':', 5);
                    if (campos[0].Trim().Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        linhas[i] = $"{campos[0]}:{campos[1]}:{campos[2]}:{campos[3]}:{DateTime.Now:yyyy-MM-ddTHH:mm:ss}";
                        alterado = true;
                        break;
                    }
                }

                if (alterado)
                    File.WriteAllLines(csvPath, linhas);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO ESCRITA] Não foi possível atualizar o CSV: {ex.Message}");
        }
    }

    static void AtualizarEstadoSensor(string id, string novoEstado)
    {
        try
        {
            lock (lockCsv)
            {
                List<string> linhas = File.ReadAllLines(csvPath).ToList();
                bool alterado = false;

                for (int i = 0; i < linhas.Count; i++)
                {
                    string[] campos = linhas[i].Split(':', 5);
                    if (campos[0].Trim().Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        linhas[i] = $"{campos[0]}:{novoEstado}:{campos[2]}:{campos[3]}:{campos[4]}";
                        alterado = true;
                        break;
                    }
                }

                if (alterado)
                    File.WriteAllLines(csvPath, linhas);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO ESTADO] {ex.Message}");
        }
    }

    static void ReiniciarLastSync()
    {
        try
        {
            lock (lockCsv)
            {
                var linhas = File.ReadAllLines(csvPath).ToList();
                for (int i = 0; i < linhas.Count; i++)
                {
                    var campos = linhas[i].Split(':', 5);
                    if (campos.Length >= 5 && campos[1].Trim() == "ativo")
                        linhas[i] = $"{campos[0]}:{campos[1]}:{campos[2]}:{campos[3]}:{DateTime.Now:yyyy-MM-ddTHH:mm:ss}";
                }
                File.WriteAllLines(csvPath, linhas);
            }
            Console.WriteLine("[SISTEMA] Timestamps dos sensores ativos reiniciados.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO] Não foi possível reiniciar timestamps: {ex.Message}");
        }
    }

    static void NotificarServidorEstado(string sensorId, string estado)
    {
        try
        {
            using var client = new TcpClient("127.0.0.1", 9000);
            using var w = new StreamWriter(client.GetStream()) { AutoFlush = true };
            using var r = new StreamReader(client.GetStream());
            w.WriteLine($"SENSOR_ESTADO_CHANGE {sensorId} {estado}");
            r.ReadLine();
        }
        catch { /* servidor indisponível */ }
    }

    static void VerificarSensoresOffline()
    {
        string[] linhas;

        try
        {
            lock (lockCsv)
            {
                linhas = File.ReadAllLines(csvPath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO OFFLINE] {ex.Message}");
            return;
        }

        foreach (string linha in linhas)
        {
            string[] campos = linha.Split(':', 5);

            if (campos.Length < 5) continue;

            string id = campos[0];
            string estado = campos[1];
            string lastSyncStr = campos[4];

            if (!DateTime.TryParse(lastSyncStr, out DateTime lastSync)) continue;

            double diferenca = (DateTime.Now - lastSync).TotalSeconds;

            if (diferenca > 30 && estado == "ativo" && lastSync >= gatewayStartTime)
            {
                Console.WriteLine($"[ALERTA] Sensor {id} OFFLINE há {diferenca:F0}s -> Estado alterado para 'manutencao'");
                AtualizarEstadoSensor(id, "manutencao");
                NotificarServidorEstado(id, "manutencao");
            }
        }
    }
}
