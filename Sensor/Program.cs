using System;
using System.Net.Sockets;
using System.IO;

class Sensor
{
    static void Main()
    {
        TcpClient client = new TcpClient("127.0.0.1", 8000);

        StreamReader reader = new StreamReader(client.GetStream());
        StreamWriter writer = new StreamWriter(client.GetStream()) { AutoFlush = true };

        Console.WriteLine("Ligado ao Gateway!");

        while (true)
        {
            Console.Write(">> ");
            string input = Console.ReadLine();

            writer.WriteLine(input);

            string response = reader.ReadLine();
            Console.WriteLine("Resposta: " + response);
        }
    }
}