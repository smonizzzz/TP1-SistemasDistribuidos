using System;
using System.Net.Sockets;
using System.IO;
using System.Threading;

class Sensor
{
    static void Main()
    {
        //  ID do sensor
        Console.Write("Introduz o ID do sensor (ex: S101): ");
        string sensorId = Console.ReadLine();

        //  Ligar ao Gateway
        TcpClient client = new TcpClient("127.0.0.1", 8000);

        StreamReader reader = new StreamReader(client.GetStream());
        StreamWriter writer = new StreamWriter(client.GetStream()) { AutoFlush = true };

        Console.WriteLine("Ligado ao Gateway!");

       
        writer.WriteLine($"CONNECT {sensorId}");
        string response = reader.ReadLine();
        Console.WriteLine("Resposta: " + response);

      
        string[] tipos = { "TEMP", "HUM", "RUIDO","PM10" ,"PM25" ,"CO2" ,"UV" };

        writer.WriteLine("TYPES " + string.Join(",", tipos));
        reader.ReadLine();

       
        new Thread(() =>
        {
            while (true)
            {
                writer.WriteLine("HEARTBEAT");
                Thread.Sleep(5000);
            }
        }).Start();

       
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
        }).Start();

   
        Console.WriteLine("Escreve 'EXIT' para terminar o sensor.");

        while (true)
        {
            string input = Console.ReadLine();

            if (input.ToUpper() == "EXIT")
                break;
        }

        writer.WriteLine("DISCONNECT");
        client.Close();

        Console.WriteLine("Sensor desligado corretamente.");
    }
}