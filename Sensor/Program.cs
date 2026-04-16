using System;
using System.Net.Sockets;
using System.Threading;
using System.Linq;

class Sensor
{
    static readonly object writerLock = new object();

    static void Main(string[] args)
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

        string opcao = Console.ReadLine();

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

        Console.Write("Introduz o ID do sensor (ex: S_NRT_001): ");
        string sensorId = Console.ReadLine()?.Trim() ?? "";

        if (string.IsNullOrEmpty(sensorId))
        {
            Console.WriteLine("ID inválido. A terminar.");
            return;
        }

        TcpClient client;
        try
        {
            client = new TcpClient();
            client.Connect(gatewayIp, gatewayPort);
            client.ReceiveTimeout = 10000;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERRO] Não foi possível ligar ao Gateway: {ex.Message}");
            return;
        }

        StreamReader reader = new StreamReader(client.GetStream());
        StreamWriter writer = new StreamWriter(client.GetStream()) { AutoFlush = true };

        Console.WriteLine($"Ligado ao Gateway {gatewayIp}:{gatewayPort}");

        // Handshake inicial (síncrono, antes de lançar threads)
        lock (writerLock) writer.WriteLine($"CONNECT {sensorId}");
        string response = reader.ReadLine();
        Console.WriteLine("Resposta: " + response);

        if (response != "ACK_OK")
        {
            Console.WriteLine("Ligação recusada pelo Gateway. A terminar.");
            client.Close();
            return;
        }

        string[] tipos = { "TEMP", "HUM", "RUIDO", "PM10", "PM25", "CO2", "UV", "AR" };

        lock (writerLock) writer.WriteLine("TYPES " + string.Join(",", tipos));
        reader.ReadLine();

        // Thread leitora — consome todas as respostas do Gateway sem bloquear o main
        new Thread(() =>
        {
            try
            {
                string? resp;
                while ((resp = reader.ReadLine()) != null)
                    Console.WriteLine($"[Gateway] {resp}");
            }
            catch { }
        }) { IsBackground = true }.Start();

        // Thread de Heartbeat (protegida com lock)
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    lock (writerLock) writer.WriteLine("HEARTBEAT");
                    Thread.Sleep(5000);
                }
                catch { break; }
            }
        }) { IsBackground = true }.Start();

        // Menu de utilizador
        Console.WriteLine("\n========================================");
        Console.WriteLine("Comandos disponíveis:");
        Console.WriteLine("  SEND <tipo> <valor>  - Enviar medição (ex: SEND TEMP 25)");
        Console.WriteLine("  TIPOS                - Listar tipos disponíveis");
        Console.WriteLine("  AUTO                 - Ativar envio automático a cada 7s");
        Console.WriteLine("  EXIT                 - Desligar sensor");
        Console.WriteLine("========================================\n");

        bool autoMode = false;

        while (true)
        {
            Console.Write("> ");
            string input = Console.ReadLine()?.Trim() ?? "";

            if (string.IsNullOrEmpty(input)) continue;

            if (input.Equals("EXIT", StringComparison.OrdinalIgnoreCase)) break;

            if (input.Equals("TIPOS", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Tipos disponíveis: " + string.Join(", ", tipos));
                continue;
            }

            if (input.Equals("AUTO", StringComparison.OrdinalIgnoreCase))
            {
                if (autoMode)
                {
                    Console.WriteLine("[AUTO] Modo automático já está ativo.");
                    continue;
                }
                autoMode = true;
                Console.WriteLine("[AUTO] Modo automático ativado — a enviar dados a cada 7 segundos.");

                new Thread(() =>
                {
                    Random rnd = new Random();
                    while (autoMode)
                    {
                        try
                        {
                            string tipo = tipos[rnd.Next(tipos.Length)];
                            double valor = rnd.Next(10, 100);
                            string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                            lock (writerLock) writer.WriteLine($"DATA {tipo} {valor} {timestamp}");
                            Console.WriteLine($"[AUTO] Enviado: {tipo}={valor}");
                            Thread.Sleep(7000);
                        }
                        catch { break; }
                    }
                }) { IsBackground = true }.Start();
                continue;
            }

            string[] parts = input.Split(' ');
            if (parts[0].Equals("SEND", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3)
            {
                string tipo = parts[1].ToUpper();
                string valor = parts[2];

                if (!tipos.Contains(tipo))
                {
                    Console.WriteLine($"[ERRO] Tipo '{tipo}' desconhecido. Usa TIPOS para ver os disponíveis.");
                    continue;
                }

                string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                lock (writerLock) writer.WriteLine($"DATA {tipo} {valor} {timestamp}");
                continue;
            }

            Console.WriteLine("Comando inválido. Usa SEND <tipo> <valor>, TIPOS, AUTO ou EXIT.");
        }

        autoMode = false;

        lock (writerLock) writer.WriteLine("DISCONNECT");
        Thread.Sleep(300);
        client.Close();

        Console.WriteLine("Sensor desligado corretamente.");
    }
}
