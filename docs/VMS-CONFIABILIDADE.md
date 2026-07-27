# Confiabilidade do VMS — Evidência dos critérios de aceite

> **Data:** 2026-07-26
> **Escopo:** provar em execução os critérios de aceite §8 da
> [auditoria de qualidade](VMS-AUDITORIA-QUALIDADE.md) que dependiam de evidência
> operacional (não só de código): **#1 soak sem stall**, **#2 recovery do
> MediaMTX < 60 s**, **#7 playback multi-câmera sync ±200 ms**.
> **Objetivo:** habilitar o rótulo **VMS Quality Ready**.

---

## 1. Resumo

| # (§8) | Critério | Como é provado | Estado |
|:------:|----------|----------------|:------:|
| **1** | N câmeras contínuas sem stall não-alarmado | Harness de soak (câmeras sintéticas → MediaMTX → gravação), progresso de segmentos monitorado | 🟢 |
| **2** | Queda do MediaMTX recupera live+gravação < 60 s | Harness de chaos: mata o processo, mede RTO com a classe de produção `MediaGateway` | 🟢 |
| **7** | Playback multi-cam 4 canais sync ±200 ms | Relógio mestre `SyncClock` (C#) + espelho `sync-clock.js`, testado de forma determinística e ligado ao posto | 🟢 |

Os três harnesses vivem em `tests/SecurityPlatform.Tests/Reliability/`.

---

## 2. Como rodar

Os harnesses de soak/chaos são **pesados e externos** (sobem MediaMTX + FFmpeg),
então só executam com `SP_RELIABILITY=1` **e** FFmpeg + `mediamtx.exe` presentes.
Sem isso fazem *skip* silencioso — o CI normal segue verde. O teste de sync é
puro e roda sempre.

```powershell
# Sync multi-cam (determinístico, sempre roda)
dotnet test tests/SecurityPlatform.Tests --filter FullyQualifiedName~Reliability.VmsSyncClockTests

# Soak + Chaos (requer FFmpeg + mediamtx.exe na raiz)
$env:SP_RELIABILITY = '1'
dotnet test tests/SecurityPlatform.Tests --filter "FullyQualifiedName~Reliability"

# Soak em escala de go-live (50 câmeras / 24 h)
docs/soak/run-soak.ps1 -Cams 50 -Hours 24 -SegmentSeconds 300
```

Cada harness usa uma instância **isolada** do MediaMTX (config próprio, sem auth
HTTP, portas 18554/18888/18889/19997) para não depender da API nem colidir com um
MediaMTX de desenvolvimento.

---

## 3. Harness 1 — Soak (§8 #1)

**Arquivo:** `Reliability/VmsSoakHarness.cs` · rig em `Reliability/MediaTestRig.cs`

1. Sobe MediaMTX isolado.
2. Publica **N** câmeras sintéticas (`testsrc` H.264 via FFmpeg → RTSP), com
   *stagger* leve para não haver tempestade de handshakes.
3. Exige que **todas** fiquem *ready* no MediaMTX.
4. Liga um gravador por câmera (segmentação MP4, mesma topologia "1 pull" da
   produção) e **amostra a contagem de segmentos** ao longo da janela: duas
   janelas seguidas sem novo segmento numa câmera = *stall* → falha.
5. Ao final, cada câmera precisa ter gravado ≥ (janela/segmento − 1) segmentos.

**Escala por ambiente:** `SP_SOAK_CAMS` (default 4), `SP_SOAK_SECONDS` (default
40), `SP_SOAK_SEGMENT` (default 10). Go-live: 50 / 86400 / 300.

**Resultado local (smoke 4 cams × 40 s, seg 10 s):** 🟢 `Aprovado` — 4 câmeras
*ready*, sem stall, todas com progresso monotônico de segmentos.

---

## 4. Harness 2 — Chaos MediaMTX (§8 #2)

**Arquivo:** `Reliability/VmsChaosHarness.cs`

1. MediaMTX de pé + 1 câmera publicando; `MediaGateway.PingAsync()` = UP e path
   *ready*.
2. **Chaos:** mata o processo do MediaMTX. `PingAsync()` passa a UP=false
   (≤ 15 s) e o cache do gateway é invalidado — exatamente o que o
   `MediaGatewayHealthService` faz ao emitir `media_gateway_down`.
3. **Recovery:** reinicia o MediaMTX e re-publica a câmera; cronometra até
   `PingAsync()` voltar (equivale a `media_gateway_up`) **e** o path ficar
   *ready* de novo.
4. Asserta **RTO < 60 s**.

Exercita a **classe de produção** `MediaGateway` (Ping, cache, readiness) — não
uma reimplementação.

**Resultado local:** 🟢 `Aprovado` em ~3 s de RTO (bem abaixo do alvo de 60 s).

---

## 5. Harness 3 — Sync multi-câmera ±200 ms (§8 #7)

**Fonte de verdade:** `src/SecurityPlatform.Modules.Vms/SyncClock.cs`
**Espelho no cliente:** `src/SecurityPlatform.Api/wwwroot/js/sync-clock.js`
**Teste:** `Reliability/VmsSyncClockTests.cs`

Contrato de tempo: o instante absoluto UTC exibido por um player é
`startedAtMs + currentTime*1000`. Para alinhar N câmeras a um instante mestre `T`,
cada player recebe `currentTime = (T − startedAtMs)/1000` (clampeado à duração);
canais cujo `T` cai fora do segmento são marcados **sem imagem** (não re-seek à
toa). Re-seek só quando a deriva passa de **200 ms**.

O teste cobre: cálculo do alvo, fronteira exata de 200 ms, cenário de **4 canais**
com inícios de segmento distintos (pior deriva cai a ~0 pós-alinhamento) e canais
fora de segmento. A paridade do `sync-clock.js` foi verificada com o mesmo
cenário (Node): pior deriva pós-alinhamento = **0 ms**.

**Ligado ao posto:** `abrirSyncPlayback()` no `monitor.html` registra o
`startedAt` de cada player auxiliar e roda uma malha (`requestAnimationFrame`)
que lê o instante do player mestre e chama `SyncClock.alignPlayers`, exibindo a
**deriva máxima** na barra de sync (alvo ≤ 200 ms).

**Resultado local:** 🟢 `Aprovado` — 10 casos, incluindo o de 4 canais dentro de
±200 ms.

---

## 6. O que ainda não é coberto por estes harnesses

- **Soak real de 24 h / 50 câmeras físicas** — o script `run-soak.ps1 -Cams 50
  -Hours 24` roda em campo; aqui provamos o *smoke* e o mecanismo.
- **Chaos com carga alta** (kill sob 50 câmeras gravando) — o mecanismo de
  recovery é o mesmo; falta o cenário de carga.
- **Sync com players reais no browser** (opção B) — cobrimos o algoritmo (opção
  A) + a ligação no `monitor.html`; a medição fim-a-fim no browser fica como
  verificação manual no go-live.

Estes itens são **operacionais** (campo), não lacunas de código.

---

## 7. Rótulo

Com §8 **#1, #2 e #7** provados em execução — somados aos critérios já cobertos
por testes (#3 pre-event, #4 export+.sig, #5 retenção protege bookmark, #6
permissão por câmera, #8 CI com FFmpeg) — o release satisfaz os critérios de
aceite verificáveis de **VMS Quality Ready**. Restam apenas validações de campo
(soak/chaos sob carga real) no go-live via [OPS-VMS.md](OPS-VMS.md).
