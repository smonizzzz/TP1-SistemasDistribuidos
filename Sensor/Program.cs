using System;
using System.Net.Sockets;
using System.IO;
using System.Threading;

class Sensor
{
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
        string sensorId = Console.ReadLine();

        TcpClient client = new TcpClient(gatewayIp, gatewayPort);

        StreamReader reader = new StreamReader(client.GetStream());
        StreamWriter writer = new StreamWriter(client.GetStream()) { AutoFlush = true };

        Console.WriteLine($"Ligado ao Gateway {gatewayIp}:{gatewayPort}");

        writer.WriteLine($"CONNECT {sensorId}");
        string response = reader.ReadLine();
        Console.WriteLine("Resposta: " + response);

        if (response != "ACK_OK")
        {
            Console.WriteLine("Ligação recusada pelo Gateway. A terminar.");
            client.Close();
            return;
        }

        string[] tipos = { "TEMP", "HUM", "RUIDO", "PM10", "PM25", "CO2", "UV", "AR" };

        writer.WriteLine("TYPES " + string.Join(",", tipos));
        reader.ReadLine();

        // Thread de Heartbeat
        new Thread(() =>
        {
            while (true)
            {
                writer.WriteLine("HEARTBEAT");
                Thread.Sleep(5000);
            }
        }) { IsBackground = true }.Start();

        // Thread de envio de dados
        new Thread(() =>
        {
            Random rnd = new Random();

            while (true)
            {
                string tipo = tipos[rnd.Next(tipos.Length)];
                double valor = rnd.Next(10, 100);
                string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                writer.WriteLine($"DATA {tipo} {valor} {timestamp}");

                Thread.Sleep(7000);
            }
        }) { IsBackground = true }.Start();

        Console.WriteLine("Escreve 'EXIT' para terminar o sensor.");

        while (true)
        {
            string input = Console.ReadLine();

            if (input?.ToUpper() == "EXIT")
                break;
        }

        writer.WriteLine("DISCONNECT");
        client.Close();

        Console.WriteLine("Sensor desligado corretamente.");
    }
}
