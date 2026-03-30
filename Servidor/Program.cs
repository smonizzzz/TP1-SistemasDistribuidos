using System;
using System.Net;
using System.Net.Sockets;
using System.IO;

class Servidor 
{
    static void Main()
    {
        // O Servidor escuta na porta 9000 (conforme configurado na Gateway)
        TcpListener server = new TcpListener(IPAddress.Any, 9000);
        server.Start();
        
        Console.WriteLine("================================================");
        Console.WriteLine(">>> SERVIDOR ONE HEALTH - ATIVO");
        Console.WriteLine(">>> A aguardar dados da Gateway na porta 9000...");
        Console.WriteLine("================================================");

        while (true)
        {
            try
            {
                using (TcpClient gatewayClient = server.AcceptTcpClient())
                using (StreamReader reader = new StreamReader(gatewayClient.GetStream()))
                using (StreamWriter writer = new StreamWriter(gatewayClient.GetStream()) { AutoFlush = true })
                {
                    string message;
                    while ((message = reader.ReadLine()) != null)
                    {
                        // Exemplo recebido: FORWARD_DATA S101 ZONA_CENTRO TEMP 22.5 2026-03-30T22:30:00
                        string[] p = message.Split(' ');

                        if (p[0] == "FORWARD_DATA" && p.Length >= 6)
                        {
                            string id = p[1];
                            string zona = p[2];
                            string tipo = p[3];
                            string valor = p[4];
                            string timestamp = p[5];

                            // 1. Criar a linha de registo organizada
                            string log = $"{timestamp} | ID:{id} | Zona:{zona} | Valor:{valor}\n";

                            // 2. Nome do ficheiro baseado no tipo de dado (ex: dados_TEMP.txt)
                            string nomeFicheiro = $"dados_{tipo.Replace("[", "").Replace("]", "")}.txt";

                            // 3. Gravar no ficheiro (AppendAllText cria o ficheiro se não existir)
                            File.AppendAllText(nomeFicheiro, log);

                            Console.WriteLine($"[LOGADO] Tipo: {tipo} | Valor: {valor} -> Gravado em {nomeFicheiro}");
                        }
                        
                        // Responder à Gateway que o Servidor recebeu com sucesso
                        writer.WriteLine("ACK_OK");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Conexão com Gateway interrompida: {ex.Message}");
            }
        }
    }
}