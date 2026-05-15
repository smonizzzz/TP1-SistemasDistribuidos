using Grpc.Core;
using System.Globalization;

namespace PreProcessamento.Services;

public class PreProcessadorImpl : global::PreProcessamento.PreProcessamentoService.PreProcessamentoServiceBase
{
    // Limites válidos por tipo de sensor
    static readonly Dictionary<string, (double min, double max)> limites = new()
    {
        ["TEMP"]  = (-10.0, 60.0),
        ["HUM"]   = (0.0,  100.0),
        ["CO2"]   = (300.0, 5000.0),
        ["PM25"]  = (0.0,  500.0),
        ["PM10"]  = (0.0,  600.0),
        ["RUIDO"] = (0.0,  140.0),
        ["UV"]    = (0.0,   11.0),
        ["AR"]    = (0.0,  500.0),
    };

    // Fatores de conversão (normaliza para unidade padrão)
    static readonly Dictionary<string, double> fatoresConversao = new()
    {
        ["TEMP"]  = 1.0,  // já em Celsius
        ["HUM"]   = 1.0,  // já em %
        ["CO2"]   = 1.0,  // já em ppm
        ["PM25"]  = 1.0,  // já em µg/m³
        ["PM10"]  = 1.0,  // já em µg/m³
        ["RUIDO"] = 1.0,  // já em dB
        ["UV"]    = 1.0,  // já em índice UV
        ["AR"]    = 1.0,  // já em AQI
    };

    public override Task<DadosProcessados> ProcessarDados(DadosBrutos request, ServerCallContext context)
    {
        Console.WriteLine($"[RPC] Recebido: Sensor={request.SensorId} Tipo={request.Tipo} Valor={request.Valor}");

        string tipo = request.Tipo.ToUpper().Trim();
        double valor = request.Valor;

        // Aplicar fator de conversão
        if (fatoresConversao.TryGetValue(tipo, out double fator))
            valor = Math.Round(valor * fator, 2);

        // Validar se o valor está dentro dos limites esperados
        bool valido = true;
        string mensagem = "OK";

        if (limites.TryGetValue(tipo, out var lim))
        {
            if (valor < lim.min || valor > lim.max)
            {
                valido = false;
                mensagem = $"Valor {valor} fora do intervalo válido [{lim.min}, {lim.max}] para {tipo}";
                Console.WriteLine($"[RPC] AVISO: {mensagem}");
            }
        }
        else
        {
            mensagem = $"Tipo '{tipo}' desconhecido — dados aceites sem validação";
            Console.WriteLine($"[RPC] {mensagem}");
        }

        // Normalizar timestamp para formato ISO
        string ts = request.Timestamp;
        if (DateTime.TryParse(ts, out DateTime dt))
            ts = dt.ToString("yyyy-MM-ddTHH:mm:ss");

        var resposta = new DadosProcessados
        {
            SensorId  = request.SensorId,
            Zona      = request.Zona,
            Tipo      = tipo,
            Valor     = valor,
            Timestamp = ts,
            Valido    = valido,
            Mensagem  = mensagem
        };

        Console.WriteLine($"[RPC] Resposta: Valor={valor} Valido={valido} Msg={mensagem}");
        return Task.FromResult(resposta);
    }
}
