# Rinha de Backend 2025 - API em .NET 10 com AOT

API de pagamentos desenvolvida para o desafio [Rinha de Backend 2025](https://github.com/zanfranceschi/rinha-de-backend-2025), com foco em performance extrema, inicialização instantânea e concorrência eficiente. A aplicação é construída com .NET 10 em modo AOT (Ahead-of-Time), garantindo uso mínimo de recursos e latência reduzida.

## Leia no meu blog os insights que tive dessa versão:
http://micheloliveira.com/blog/desafio-performance-rinha-backend-2025-insights-csharp-postgresql-redis/

*PS: Nem todas as implementações feitas neste projeto são recomendadas para cenários reais de produção e algumas delas foram desenvolvidas especificamente para o desafio Rinha de Backend 2025.*

## Execução local:
```json
{
  "participante": "anonymous",
  "total_liquido": 363940.155,
  "total_bruto": 333126,
  "total_taxas": 16656.3,
  "descricao": "'total_liquido' é sua pontuação final. Equivale ao seu lucro. Fórmula: total_liquido + (total_liquido * p99.bonus) - (total_liquido * multa.porcentagem)",
  "p99": {
    "valor": "3.31ms",
    "bonus": 0.15,
    "max_requests": "550",
    "descricao": "Fórmula para o bônus: max((11 - p99.valor) * 0.02, 0)"
  },
  "multa": {
    "porcentagem": 0,
    "total": 0,
    "composicao": {
      "total_inconsistencias": 0,
      "descricao": "Se 'total_inconsistencias' > 0, há multa de 35%."
    }
  }
}
```

## Stack

- **.NET 10 (AOT)** - Gerando um executável nativo
- **[ReactiveLock](https://www.nuget.org/packages/ReactiveLock.Distributed.Redis/)** - Lock distribuído e reativo via Redis para maximinizar a consistência entre as instâncias
- **PostgreSQL e Redis** - Persistência dos dados e enfileiramento de mensagens
- **Dapper + Dapper.AOT** - ORM leve e compatível com AOT
- **Polly** - Política de retry resiliente para conexões externas
- **Nginx** - Proxy reverso para balanceamento de carga entre as duas instâncias
- **Docker Compose** - 1.5 CPU e 350MB de RAM no total, conforme as regras da [Rinha de Backend 2025](https://github.com/zanfranceschi/rinha-de-backend-2025)

```mermaid

graph TD
  loadBalancer["<b>Load Balancer</b><br />(NGINX 1.29.0-alpine)"]

  subgraph backendsGroup["<b>BACKENDS</b>"]
    backend1["<b>Backend 1 AOT</b><br />(.NET 10.0-alpine)"]
    backend2["<b>Backend 2 AOT</b><br />(.NET 10.0-alpine)"]
    reactiveLock["<b>Lock Reativo Distribuído</b><br/>(lib <b>ReactiveLock</b> para sincronia entre processos<br/>HTTP, PostGres, API de Sumário)"]
  end

  subgraph storageGroup["<b>ARMAZENAMENTO E MENSAGERIA</b>"]
    postgres["<b>PostgreSQL</b><br />(postgres:17-alpine)"]
    spacer[" "]:::invisible
    redis["<b>Redis</b><br />(redis:8-alpine)"]
  end

  loadBalancer --> backendsGroup

  backend1 --> storageGroup
  backend2 --> storageGroup

  backend1 --> reactiveLock
  backend2 --> reactiveLock

  reactiveLock --> storageGroup

  classDef invisible fill:none,stroke:none;

  %% Estilos de cor
  style loadBalancer fill:#256D85,stroke:#1B4B57,stroke-width:2px,color:#FFFFFF

  style backendsGroup fill:#a8c7ff,stroke:#333,stroke-width:2px,color:#000
  style backend1 fill:#c9ddff,stroke:#333,stroke-width:1px,color:#000
  style backend2 fill:#c9ddff,stroke:#333,stroke-width:1px,color:#000
  style reactiveLock fill:#f2c14e,stroke:#b8860b,stroke-width:2px,color:#000

  style storageGroup fill:#a8d5a2,stroke:#333,stroke-width:2px,color:#000
  style postgres fill:#c6e0b4,stroke:#333,stroke-width:1px,color:#000
  style redis fill:#c6e0b4,stroke:#333,stroke-width:1px,color:#000
  

```

## Endpoints

- `POST /payments` - Enfileira um pagamento para processamento assíncrono
- `GET /payments-summary` - Retorna um resumo agregado dos pagamentos
- `POST /purge-payments` - Remove os registros de pagamento do sistema

---

## Especificações arquiteturais

- Uso da biblioteca [ReactiveLock](https://www.nuget.org/packages/ReactiveLock.Distributed.Redis/) garante consistência máxima entre múltiplas instâncias sem perda de integridade. Esse, por sua vez, também reage e controla o estado de processamento das requisições HTTP, PostgreSQL e API de sumário entre as instâncias.
- Utiliza inserção em lote (bulk insert) de 100 registros no `PostgreSQL` para otimizar a performance com consistência sincronizada entre as instâncias.
- O lock coordenado ocorre especialmente quando a chamada ao endpoint de `GET /payments-summary` é realizada, sincronizando o flush dos lotes do postgres para manter a integridade dos dados antes de realizar a query no banco de dados.
- O `Redis` utilizado para enfileirar as mensagens recebidas garante um pool consistente e balanceado de workers, diferente do envio das requisições para uma única instância via round robin, que não assegura balanceamento adequado devido à variabilidade no tempo de execução entre as requisições a api de pagamentos.
- Totalmente compatível com build AOT, sem reflection dinâmica nem expressões incompatíveis.
- Pronto para ambientes de alta concorrência, ideal para benchmarks e cenários de stress.

## Como rodar

### Subir a stack com Docker Compose
#### 0. Instale o docker caso não tenha:

[Instruções aqui](https://docs.docker.com/get-started/get-docker/)

#### 1. Neste repositório, esses comandos irão compilar a aplicação em AOT e subir Redis, PostgreSQL e NGINX para uso local:
```bash
cd src
docker compose build --no-cache
docker compose up -d
```
#### 2. Instalar o K6:

[Instruções aqui](https://grafana.com/docs/k6/latest/set-up/install-k6/)

#### 3. Clonar o repositório da [Rinha de Backend 2025](https://github.com/zanfranceschi/rinha-de-backend-2025) para subir a api de processamento de pagamentos e executar os seguintes comandos:
```bash
cd payment-processor
docker compose up -d
```
#### 4. Com o K6 instalado, rodar o teste com o seguinte comando dentro do repositório da [Rinha de Backend 2025](https://github.com/zanfranceschi/rinha-de-backend-2025):
```bash
cd rinha-test
k6 run -e MAX_REQUESTS=550 rinha.js
```

Instruções, atualizadas em 2025-07-27 do [MINIGUIA - Rinha de Backend 2025](https://github.com/zanfranceschi/rinha-de-backend-2025/blob/main/rinha-test/MINIGUIA.md)

## Licença

MIT © Michel Oliveira

Para sugestões, dúvidas ou contribuições, fique à vontade para abrir uma issue ou pull request.
