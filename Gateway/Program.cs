using System;
using System.Net;
using System.Net.Sockets;
using System.IO;

class Gateway
{
    static void Main()
    {
        TcpListener listener = new TcpListener(IPAddress.Any, 8000);
        listener.Start();
        Console.WriteLine("Gateway à escuta na porta 8000...");

        while (true)
        {
            TcpClient sensorClient = listener.AcceptTcpClient();
            Console.WriteLine("Sensor ligado!");

            StreamReader reader = new StreamReader(sensorClient.GetStream());
            StreamWriter writer = new StreamWriter(sensorClient.GetStream()) { AutoFlush = true };

            // ligação ao servidor
            TcpClient serverClient = new TcpClient("127.0.0.1", 9000);
            StreamWriter serverWriter = new StreamWriter(serverClient.GetStream()) { AutoFlush = true };
            StreamReader serverReader = new StreamReader(serverClient.GetStream());

            string sensorId = "";

            string message;
            while ((message = reader.ReadLine()) != null)
            {
                Console.WriteLine("Sensor -> Gateway: " + message);

                string[] parts = message.Split(' ');
                string comando = parts[0];

                // 🔵 CONNECT
                if (comando == "CONNECT" && parts.Length >= 2)
                {
                    sensorId = parts[1];

                    writer.WriteLine("ACK_OK");

                    // notificar servidor
                    serverWriter.WriteLine($"SENSOR_CONNECT {sensorId} ZONA_TESTE");
                    serverReader.ReadLine(); // lê ACK do servidor
                }

                // 🟢 DATA
                else if (comando == "DATA" && parts.Length >= 4)
                {
                    string tipo = parts[1];
                    string valor = parts[2];
                    string timestamp = parts[3];

                    writer.WriteLine("ACK_OK");

                    // envia para servidor no formato correto
                    serverWriter.WriteLine($"FORWARD_DATA {sensorId} ZONA_TESTE {tipo} {valor} {timestamp}");
                    serverReader.ReadLine();
                }

                // 🔴 DISCONNECT
                else if (comando == "DISCONNECT" && parts.Length >= 2)
                {
                    writer.WriteLine("ACK_BYE");

                    // notificar servidor
                    serverWriter.WriteLine($"SENSOR_DISCONNECT {sensorId}");
                    serverReader.ReadLine();

                    break; // termina ligação com sensor
                }

                // ❌ ERRO
                else
                {
                    writer.WriteLine("ACK_ERR comando_invalido");
                }
            }

            sensorClient.Close();
            serverClient.Close();
            Console.WriteLine("Ligação terminada.\n");
        }
    }
}