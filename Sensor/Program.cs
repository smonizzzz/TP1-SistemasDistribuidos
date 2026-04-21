using System;
using System.Net.Sockets;
using System.Threading;
using System.Linq;

class Sensor
{
    static readonly object writerLock = new object();
    static readonly string[] tipos = { "TEMP", "HUM", "RUIDO", "PM10", "PM25", "CO2", "UV", "AR" };

    static volatile bool running       = true;
    static volatile bool connected     = false;
    static volatile bool streaming     = false;
    static volatile bool autoOnConnect = false;
    static volatile bool emManutencao  = false;
    static string sensorId = "";
    static StreamWriter? currentWriter = null;

    static void Main(string[] args)
    {
        // Parse args: --zona N  --id X  --auto
        string? argZona = null, argId = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--zona") argZona = args[i + 1];
            if (args[i] == "--id")   argId   = args[i + 1];
        }
        autoOnConnect = args.Contains("--auto");

        // Infere zona do prefixo do ID se --zona não foi passado
        if (argZona == null && argId != null)
        {
            string up = argId.ToUpper();
            if      (up.StartsWith("S_NRT")) argZona = "1";
            else if (up.StartsWith("S_CTR")) argZona = "2";
            else if (up.StartsWith("S_SUL")) argZona = "3";
            else if (up.StartsWith("S_ILH")) argZona = "4";
        }

        string opcao;
        if (argZona != null)
        {
            opcao = argZona;
        }
        else
        {
            Console.WriteLine("========================================");
            Console.WriteLine("      SISTEMA DE MONITORIZAÇÃO");
            Console.WriteLine("========================================");
            Console.WriteLine("Escolhe a Gateway:");
            Console.WriteLine("  1. Norte  (porta 8001)");
            Console.WriteLine("  2. Centro (porta 8002)");
            Console.WriteLine("  3. Sul    (porta 8003)");
            Console.WriteLine("  4. Ilhas  (porta 8004)");
            Console.Write("Opção: ");
            opcao = Console.ReadLine() ?? "";
        }

        string gatewayIp = "127.0.0.1";
        int gatewayPort = opcao switch
        {
            "1" => 8001,
            "2" => 8002,
            "3" => 8003,
            "4" => 8004,
            _ => -1
        };

        if (gatewayPort == -1)
        {
            Console.WriteLine("Opção inválida. A terminar.");
            return;
        }

        if (argId != null)
        {
            sensorId = argId;
        }
        else
        {
            Console.Write("Introduz o ID do sensor (ex: S_NRT_001): ");
            sensorId = Console.ReadLine()?.Trim() ?? "";
        }

        if (string.IsNullOrEmpty(sensorId))
        {
            Console.WriteLine("ID inválido. A terminar.");
            return;
        }

        Console.WriteLine("\n========================================");
        Console.WriteLine("Comandos disponíveis:");
        Console.WriteLine("  SEND <tipo> <valor>  - Enviar medição (ex: SEND TEMP 25)");
        Console.WriteLine("  TIPOS                - Listar tipos disponíveis");
        Console.WriteLine("  AUTO                 - Ativar envio automático a cada 7s");
        Console.WriteLine("  VIDEO                - Iniciar stream de vídeo");
        Console.WriteLine("  STOP                 - Terminar stream de vídeo");
        Console.WriteLine("  EXIT                 - Desligar sensor");
        Console.WriteLine("========================================\n");

        // Thread de input do utilizador — corre independentemente da ligação
        new Thread(() => LoopUtilizador()) { IsBackground = true }.Start();

        // Loop principal de ligação/reconexão
        while (running)
        {
            TentarLigar(gatewayIp, gatewayPort, sensorId);

            if (running)
            {
                if (emManutencao)
                    Console.WriteLine("[SENSOR] Sensor sem resposta — a aguardar reativação... (nova tentativa em 10s)");
                else
                    Console.WriteLine("[SENSOR] Sem ligação — a tentar reconectar em 10 segundos...");
                for (int i = 0; i < 10 && running; i++)
                    Thread.Sleep(1000);
            }
        }

        Console.WriteLine("Sensor desligado corretamente.");
    }

    static void TentarLigar(string ip, int porta, string sensorId)
    {
        try
        {
            var client = new TcpClient();
            client.Connect(ip, porta);
            client.ReceiveTimeout = 10000;

            var reader = new StreamReader(client.GetStream());
            var writer = new StreamWriter(client.GetStream()) { AutoFlush = true };

            // Handshake
            lock (writerLock) writer.WriteLine($"CONNECT {sensorId}");
            string? response = reader.ReadLine();

            if (response != "ACK_OK")
            {
                if (response == "ACK_ERR_UNAUTHORIZED")
                {
                    if (!emManutencao)
                    {
                        Console.WriteLine("========================================");
                        Console.WriteLine("  SENSOR SEM RESPOSTA");
                        Console.WriteLine($"  ID: {sensorId}");
                        Console.WriteLine("  Desativado pelo administrador.");
                        Console.WriteLine("  Terminal mantido aberto.");
                        Console.WriteLine("  A aguardar reativação...");
                        Console.WriteLine("========================================");
                    }
                    emManutencao = true;
                }
                else
                {
                    Console.WriteLine($"[SENSOR] Ligação recusada: {response}");
                }
                client.Close();
                return;
            }

            lock (writerLock) writer.WriteLine("TYPES " + string.Join(",", tipos));
            reader.ReadLine();

            currentWriter = writer;
            connected = true;
            if (emManutencao)
            {
                Console.WriteLine("========================================");
                Console.WriteLine($"  SENSOR REATIVADO — {sensorId}");
                Console.WriteLine("  A retomar operação normal.");
                Console.WriteLine("========================================");
            }
            else
            {
                Console.WriteLine($"[SENSOR] Ligado com sucesso! Sensor {sensorId} ativo.");
            }
            emManutencao = false;

            // Thread leitora — deteta desativação remota
            new Thread(() =>
            {
                try
                {
                    string? resp;
                    while ((resp = reader.ReadLine()) != null)
                    {
                        if (resp == "ACK_ERR_UNAUTHORIZED")
                        {
                            emManutencao = true;
                            Console.WriteLine("\n========================================");
                            Console.WriteLine("  SENSOR SEM RESPOSTA");
                            Console.WriteLine($"  ID: {sensorId}");
                            Console.WriteLine("  Desativado pelo administrador.");
                            Console.WriteLine("  Terminal mantido aberto.");
                            Console.WriteLine("  A aguardar reativação...");
                            Console.WriteLine("========================================");
                            connected = false;
                            break;
                        }
                        else if (resp.StartsWith("ACK_ERR"))
                        {
                            Console.WriteLine($"[ERRO] Gateway recusou: {resp}");
                        }
                    }
                }
                catch { }
                connected = false;
            }) { IsBackground = true }.Start();

            // Thread de Heartbeat
            new Thread(() =>
            {
                while (connected)
                {
                    try
                    {
                        lock (writerLock) writer.WriteLine("HEARTBEAT");
                        Thread.Sleep(5000);
                    }
                    catch { connected = false; break; }
                }
            }) { IsBackground = true }.Start();

            // Modo automático via argumento --auto
            if (autoOnConnect)
            {
                Console.WriteLine("[AUTO] Modo automático ativado via argumento.");
                new Thread(() =>
                {
                    Random rnd = Random.Shared;
                    while (connected && running)
                    {
                        try
                        {
                            string tipo = tipos[rnd.Next(tipos.Length)];
                            double valor = rnd.Next(10, 100);
                            string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                            lock (writerLock) writer.WriteLine($"DATA {tipo} {valor} {timestamp}");
                            Console.WriteLine($"[AUTO] Enviado: {tipo}={valor}");
                        }
                        catch { break; }
                        Thread.Sleep(7000);
                    }
                }) { IsBackground = true }.Start();
            }

            // Aguarda até ser desligado
            while (connected && running)
                Thread.Sleep(500);

            currentWriter = null;
            try { client.Close(); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO] Não foi possível ligar: {ex.Message}");
            connected = false;
            currentWriter = null;
        }
    }

    static void LoopUtilizador()
    {
        bool autoMode = false;

        while (running)
        {
            Console.Write("> ");
            string input = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(input)) continue;

            if (input.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
            {
                streaming = false;
                running = false;
                connected = false;
                if (currentWriter != null)
                    try { lock (writerLock) currentWriter.WriteLine("DISCONNECT"); } catch { }
                break;
            }

            if (input.Equals("VIDEO", StringComparison.OrdinalIgnoreCase))
            {
                if (!connected || currentWriter == null)
                {
                    Console.WriteLine("[ERRO] Sensor não está ligado ao Gateway.");
                    continue;
                }
                if (streaming)
                {
                    Console.WriteLine("[VIDEO] Stream já está ativa. Usa STOP para parar.");
                    continue;
                }
                streaming = true;
                lock (writerLock) currentWriter.WriteLine($"VIDEO_STREAM_START {sensorId}");
                Console.WriteLine("[VIDEO] Stream iniciada. Usa STOP para terminar.");
                new Thread(() =>
                {
                    while (streaming && connected)
                    {
                        try
                        {
                            lock (writerLock) currentWriter?.WriteLine("FRAME");
                            Thread.Sleep(200);
                        }
                        catch { streaming = false; break; }
                    }
                }) { IsBackground = true }.Start();
                continue;
            }

            if (input.Equals("STOP", StringComparison.OrdinalIgnoreCase))
            {
                if (!streaming)
                {
                    Console.WriteLine("[VIDEO] Nenhuma stream ativa.");
                    continue;
                }
                streaming = false;
                if (currentWriter != null)
                    try { lock (writerLock) currentWriter.WriteLine("VIDEO_STREAM_END"); } catch { }
                Console.WriteLine("[VIDEO] Stream terminada.");
                continue;
            }

            if (input.Equals("TIPOS", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Tipos disponíveis: " + string.Join(", ", tipos));
                continue;
            }

            if (input.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
            {
                if (autoMode) { Console.WriteLine("[AUTO] Modo automático já está ativo."); continue; }
                autoMode = true;
                Console.WriteLine("[AUTO] Modo automático ativado — a enviar dados a cada 7 segundos.");

                new Thread(() =>
                {
                    Random rnd = new Random();
                    while (autoMode && running)
                    {
                        if (connected && currentWriter != null)
                        {
                            try
                            {
                                string tipo = tipos[rnd.Next(tipos.Length)];
                                double valor = rnd.Next(10, 100);
                                string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                                lock (writerLock) currentWriter.WriteLine($"DATA {tipo} {valor} {timestamp}");
                                Console.WriteLine($"[AUTO] Enviado: {tipo}={valor}");
                            }
                            catch { }
                        }
                        Thread.Sleep(7000);
                    }
                }) { IsBackground = true }.Start();
                continue;
            }

            string[] parts = input.Split(' ');
            if (parts[0].Equals("SEND", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            {
                if (!connected || currentWriter == null)
                {
                    Console.WriteLine("[ERRO] Sensor não está ligado ao Gateway.");
                    continue;
                }

                string tipo = parts[1].ToUpper();
                string valor = parts[2];

                if (!tipos.Contains(tipo))
                {
                    Console.WriteLine($"[ERRO] Tipo '{tipo}' desconhecido. Usa TIPOS para ver os disponíveis.");
                    continue;
                }

                string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                try { lock (writerLock) currentWriter.WriteLine($"DATA {tipo} {valor} {timestamp}"); }
                catch { Console.WriteLine("[ERRO] Falha ao enviar dados."); }
                continue;
            }

            Console.WriteLine("Comando inválido. Usa SEND <tipo> <valor>, TIPOS, AUTO ou EXIT.");
        }
    }
}
