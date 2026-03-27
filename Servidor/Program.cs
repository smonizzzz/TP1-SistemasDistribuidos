using System;
using System.Net;
using System.Net.Sockets;
using System.IO;

class Servidor
{
    static void Main()
    {
        TcpListener server = new TcpListener(IPAddress.Any, 9000);
        server.Start();
        Console.WriteLine("Servidor ligado na porta 9000...");

        while (true)
        {
            TcpClient client = server.AcceptTcpClient();
            Console.WriteLine("Gateway ligado!");

            StreamReader reader = new StreamReader(client.GetStream());
            StreamWriter writer = new StreamWriter(client.GetStream()) { AutoFlush = true };

            string message;
            while ((message = reader.ReadLine()) != null)
            {
                Console.WriteLine("Recebido: " + message);

                writer.WriteLine("ACK_OK");
            }
        }
    }
}