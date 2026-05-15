import grpc
import sqlite3
import math
import os
from concurrent import futures
from datetime import datetime

import analise_pb2
import analise_pb2_grpc

DB_PATH = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "Servidor", "bin", "Debug", "net8.0", "onehealth.db"
)

THRESHOLDS = {
    "TEMP":  40.0,
    "CO2":   800.0,
    "PM25":  75.0,
    "PM10":  150.0,
    "RUIDO": 85.0,
    "HUM":   90.0,
    "UV":    8.0,
    "AR":    150.0,
}


def calcular_nivel_risco(tipo: str, media: float) -> str:
    limite = THRESHOLDS.get(tipo.upper())
    if limite is None:
        return "desconhecido"
    ratio = media / limite
    if ratio < 0.5:
        return "baixo"
    elif ratio < 0.75:
        return "moderado"
    elif ratio < 1.0:
        return "elevado"
    else:
        return "critico"


def calcular_tendencia(valores: list[float]) -> str:
    if len(valores) < 2:
        return "sem dados suficientes"
    n = len(valores)
    soma_x = sum(range(n))
    soma_y = sum(valores)
    soma_xy = sum(i * v for i, v in enumerate(valores))
    soma_x2 = sum(i * i for i in range(n))
    denominador = n * soma_x2 - soma_x ** 2
    if denominador == 0:
        return "estavel"
    declive = (n * soma_xy - soma_x * soma_y) / denominador
    if declive > 0.5:
        return "a subir"
    elif declive < -0.5:
        return "a descer"
    else:
        return "estavel"


class AnaliseServicer(analise_pb2_grpc.AnaliseServiceServicer):

    def AnalisarDados(self, request, context):
        print(f"[RPC-PY] Pedido de análise: tipo={request.tipo} zona={request.zona} "
              f"sensor={request.sensor_id} inicio={request.inicio} fim={request.fim}")

        try:
            conn = sqlite3.connect(os.path.normpath(DB_PATH))
            cursor = conn.cursor()

            query = "SELECT valor FROM medicoes WHERE 1=1"
            params = []

            if request.tipo:
                query += " AND tipo = ?"
                params.append(request.tipo.upper())
            if request.zona:
                query += " AND zona = ?"
                params.append(request.zona.upper())
            if request.sensor_id:
                query += " AND sensor_id = ?"
                params.append(request.sensor_id)
            if request.inicio:
                query += " AND timestamp >= ?"
                params.append(request.inicio)
            if request.fim:
                query += " AND timestamp <= ?"
                params.append(request.fim)

            query += " ORDER BY id ASC"

            cursor.execute(query, params)
            rows = cursor.fetchall()
            conn.close()

            valores = [row[0] for row in rows]

            if not valores:
                return analise_pb2.ResultadoAnalise(
                    media=0, maximo=0, minimo=0, desvio_padrao=0,
                    nivel_risco="sem dados", tendencia="sem dados",
                    total_medicoes=0,
                    resumo="Sem medições para os critérios fornecidos."
                )

            media = sum(valores) / len(valores)
            maximo = max(valores)
            minimo = min(valores)
            variancia = sum((v - media) ** 2 for v in valores) / len(valores)
            desvio = math.sqrt(variancia)
            nivel = calcular_nivel_risco(request.tipo, media)
            tendencia = calcular_tendencia(valores)

            resumo = (
                f"Análise de {len(valores)} medições de {request.tipo or 'todos os tipos'}"
                f" na zona {request.zona or 'todas'}. "
                f"Média: {media:.2f}, Risco: {nivel}, Tendência: {tendencia}."
            )

            print(f"[RPC-PY] Resultado: media={media:.2f} risco={nivel} tendencia={tendencia} n={len(valores)}")

            return analise_pb2.ResultadoAnalise(
                media=round(media, 2),
                maximo=round(maximo, 2),
                minimo=round(minimo, 2),
                desvio_padrao=round(desvio, 2),
                nivel_risco=nivel,
                tendencia=tendencia,
                total_medicoes=len(valores),
                resumo=resumo
            )

        except Exception as e:
            print(f"[RPC-PY] ERRO: {e}")
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details(str(e))
            return analise_pb2.ResultadoAnalise()


def serve():
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=10))
    analise_pb2_grpc.add_AnaliseServiceServicer_to_server(AnaliseServicer(), server)
    server.add_insecure_port("[::]:5200")
    server.start()
    print("================================================")
    print(">>> SERVICO DE ANALISE gRPC Python (Porta 5200)")
    print("================================================")
    server.wait_for_termination()


if __name__ == "__main__":
    serve()
