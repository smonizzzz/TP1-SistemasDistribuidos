# TP1 – Serviços de Monitorização Urbana para One Health

**Licenciatura em Engenharia Informática – UTAD**  
**Sistemas Distribuídos 2025/2026**  
Hugo Paredes | Tiago Pinto | Cristiano Pendão

---

## Descrição

Sistema distribuído que simula uma infraestrutura de monitorização ambiental urbana.  
Composto por três entidades: **SENSOR**, **GATEWAY** e **SERVIDOR**.

---

## Arquitetura

```
SENSOR ──────► GATEWAY ──────► SERVIDOR
         TCP            TCP
```

- **SENSOR** – recolhe dados ambientais e envia para o GATEWAY
- **GATEWAY** – valida, agrega e encaminha dados para o SERVIDOR; processa streams de vídeo como sistema edge
- **SERVIDOR** – armazena e processa os dados recebidos, organizados por tipo

---

## Protocolo de Comunicação

### Formato das mensagens

Todas as mensagens são texto simples, terminadas com `\n`:

```
COMANDO [parâmetro1] [parâmetro2] ...\n
```

---

### Diálogo SENSOR ↔ GATEWAY

#### Mensagens enviadas pelo SENSOR

| Mensagem | Parâmetros | Descrição |
|---|---|---|
| `CONNECT` | `sensor_id` | Inicia ligação e identificação |
| `REGISTER_TYPES` | `[TEMP,HUM,PM2.5,…]` | Declara os tipos de dados suportados |
| `DATA` | `tipo_dado valor timestamp` | Envia uma medição ambiental |
| `HEARTBEAT` | `sensor_id timestamp` | Sinal periódico de disponibilidade |
| `VIDEO_STREAM_START` | `sensor_id` | Inicia uma stream de vídeo |
| `FRAME` | `<dados base64>` | Envia uma frame de vídeo |
| `VIDEO_STREAM_END` | — | Termina a stream de vídeo |
| `DISCONNECT` | `sensor_id` | Termina a sessão corretamente |

#### Respostas do GATEWAY ao SENSOR

| Mensagem | Descrição |
|---|---|
| `ACK_OK` | Operação aceite |
| `ACK_ERR <motivo>` | Operação rejeitada (ex: `sensor_desconhecido`, `tipo_invalido`, `sensor_manutencao`) |
| `ACK_HB` | Confirmação de heartbeat |
| `ACK_BYE` | Confirmação de desligamento |

---

### Diálogo GATEWAY ↔ SERVIDOR

| Mensagem | Parâmetros | Descrição |
|---|---|---|
| `SENSOR_CONNECT` | `sensor_id zona` | Notifica ligação de um sensor |
| `FORWARD_DATA` | `sensor_id zona tipo_dado valor timestamp` | Encaminha medição para armazenamento |
| `SENSOR_DISCONNECT` | `sensor_id` | Notifica desligamento de um sensor |

#### Respostas do SERVIDOR ao GATEWAY

| Mensagem | Descrição |
|---|---|
| `ACK_OK` | Dados recebidos e armazenados |
| `ACK_ERR <motivo>` | Erro no processamento |

---

## Diagrama de Sequência

### Fase 1 – Ligação e identificação

```
SENSOR                  GATEWAY                 SERVIDOR
  |                        |                        |
  |── CONNECT sensor_id ──►|                        |
  |                        | [valida CSV]            |
  |◄── ACK_OK ────────────|                        |
  |                        |                        |
  |── REGISTER_TYPES [...] ►|                        |
  |◄── ACK_OK ────────────|                        |
  |                        |── SENSOR_CONNECT ─────►|
  |                        |◄── ACK_OK ────────────|
```

### Fase 2 – Envio de medições

```
SENSOR                  GATEWAY                 SERVIDOR
  |                        |                        |
  |── DATA tipo val ts ───►|                        |
  |                        | [valida tipo, regista]  |
  |◄── ACK_OK ────────────|                        |
  |                        |── FORWARD_DATA ────────►|
  |                        |◄── ACK_OK ────────────|
```

### Fase 3 – Stream de vídeo (opcional / edge processing)

```
SENSOR                  GATEWAY
  |                        |
  |── VIDEO_STREAM_START ──►|
  |◄── ACK_OK ────────────|
  |── FRAME <dados> ───────►| [edge processing]
  |── FRAME <dados> ───────►| [edge processing]
  |── VIDEO_STREAM_END ────►|
  |◄── ACK_OK ────────────|
```

### Fase 4 – Heartbeat periódico

```
SENSOR                  GATEWAY
  |                        |
  |── HEARTBEAT sid ts ───►| [atualiza last_sync no CSV]
  |◄── ACK_HB ────────────|
  
  [se não receber HB após timeout → sensor marcado como indisponível]
```

### Fase 5 – Finalização

```
SENSOR                  GATEWAY                 SERVIDOR
  |                        |                        |
  |── DISCONNECT sid ─────►|                        |
  |                        | [atualiza CSV estado]   |
  |                        |── SENSOR_DISCONNECT ───►|
  |                        |◄── ACK_OK ────────────|
  |◄── ACK_BYE ───────────|                        |
```

---

## Diagrama de Estados do SENSOR

```
         CONNECT              ACK_OK           ACK_OK
  IDLE ──────────► CONNECTING ──────► REGISTERING ──────► ACTIVE
   ▲                    │                                     │
   │             ACK_ERR│                                     │ DISCONNECT
   │                    ▼                                     ▼
   └──────────────── IDLE                           DISCONNECTING
                                                          │ ACK_BYE
                                                          ▼
                                                         IDLE
```

| Estado | Descrição |
|---|---|
| `IDLE` | Sensor inativo, sem ligação |
| `CONNECTING` | Enviou `CONNECT`, aguarda `ACK_OK` ou `ACK_ERR` |
| `REGISTERING` | Enviou `REGISTER_TYPES`, aguarda confirmação |
| `ACTIVE` | Ligação estabelecida, pode enviar dados e heartbeat |
| `DISCONNECTING` | Enviou `DISCONNECT`, aguarda `ACK_BYE` |

---

## Estados do Sensor no CSV (GATEWAY)

O ficheiro de configuração do GATEWAY tem o formato:

```
sensor_id:estado:zona:[tipos_dados]:last_sync
```

Exemplo:
```
S101:ativo:ZONA_CENTRO:[TEMP,HUM,RUIDO]:2026-03-10T08:45:00
S102:ativo:ZONA_ESCOLAR:[PM2.5,TEMP]:2026-03-10T09:00:00
S103:manutencao:ZONA_INDUSTRIAL:[AR,PM10]:2026-03-09T18:30:00
```

| Estado | Descrição |
|---|---|
| `ativo` | Sensor em funcionamento normal |
| `manutencao` | Sensor temporariamente indisponível |
| `desativado` | Sensor removido ou desligado |

---

## Tipos de dados suportados

| Código | Dado ambiental |
|---|---|
| `TEMP` | Temperatura |
| `HUM` | Humidade |
| `AR` | Qualidade do ar |
| `RUIDO` | Nível de ruído |
| `PM2.5` | Concentração de partículas PM2.5 |
| `PM10` | Concentração de partículas PM10 |
| `LUM` | Luminosidade |
| `VIDEO` | Stream de vídeo (edge processing) |

---

## Exemplo de sessão completa

```
S→G: CONNECT S102\n
G→S: ACK_OK\n
S→G: REGISTER_TYPES [PM2.5,TEMP]\n
G→S: ACK_OK\n
G→SRV: SENSOR_CONNECT S102 ZONA_ESCOLAR\n
SRV→G: ACK_OK\n

S→G: DATA PM2.5 78 2026-03-10T09:15:00\n
G→S: ACK_OK\n
G→SRV: FORWARD_DATA S102 ZONA_ESCOLAR PM2.5 78 2026-03-10T09:15:00\n
SRV→G: ACK_OK\n

S→G: HEARTBEAT S102 2026-03-10T09:15:30\n
G→S: ACK_HB\n

S→G: DISCONNECT S102\n
G→SRV: SENSOR_DISCONNECT S102\n
SRV→G: ACK_OK\n
G→S: ACK_BYE\n
```

---

## Implementação

- Linguagem: **C#**
- Comunicação: **Sockets TCP** (`TcpClient` / `TcpListener`)
- Leitura de mensagens: **`StreamReader`** (linha a linha, separador `\n`)
- Concorrência (fase 4): **Threads** + **Mutex** por ficheiro

---

## Faseamento

| Fase | Descrição | Semana |
|---|---|---|
| 1 | Desenho do protocolo | 16–20 março |
| 2 | Implementação básica (SENSOR + GATEWAY + SERVIDOR simples) | 23–27 março |
| 3 | Operação SENSOR completa (CSV, pré-processamento, encaminhamento) | 7–10 abril |
| 4 | Atendimento concorrente (threads + mutexes) | 13–17 abril |

**Data de entrega:** 17 de abril via Moodle.

---

## Estrutura do repositório

```
TP1-Sistemas-Distribuidos/
├── Sensor/          # Projeto C# do SENSOR
├── Gateway/         # Projeto C# do GATEWAY
├── Servidor/        # Projeto C# do SERVIDOR
├── README.md        # Este ficheiro (protocolo)
├── .gitignore
└── .gitattributes
```
