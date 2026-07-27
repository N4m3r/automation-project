# Manual prático — Funcionalidades do módulo VMS

> **Público:** operador, administrador e integrador.  
> **Objetivo:** ensinar **como fazer** cada situação já implementada no sistema.  
> **Posto de operação:** `http://localhost:8080/monitor.html`  
> **Admin:** `http://localhost:8080/admin.html` (ou aba **Config** no posto)  
> **API base:** `http://localhost:8080/api`  
> Documentos relacionados: [MODULO-VMS.md](MODULO-VMS.md) · [OPS-VMS.md](OPS-VMS.md) · [PERMISSOES-E-GRUPOS.md](PERMISSOES-E-GRUPOS.md)

---

## Índice

1. [Primeiro acesso e login](#1-primeiro-acesso-e-login)  
2. [Cadastrar e testar câmera](#2-cadastrar-e-testar-câmera)  
3. [Ver ao vivo (WebRTC/HLS)](#3-ver-ao-vivo-webrtchls)  
4. [Gravação contínua](#4-gravação-contínua)  
5. [Gravação por evento + pré-alarme](#5-gravação-por-evento--pré-alarme)  
6. [Agendar janelas de gravação](#6-agendar-janelas-de-gravação)  
7. [Playback e linha do tempo](#7-playback-e-linha-do-tempo)  
8. [Busca smart (eventos + gravação)](#8-busca-smart-eventos--gravação)  
9. [Bookmarks (proteger prova)](#9-bookmarks-proteger-prova)  
10. [Exportar vídeo com prova de integridade](#10-exportar-vídeo-com-prova-de-integridade)  
11. [Máscara de privacidade](#11-máscara-de-privacidade)  
12. [PTZ, presets e tour](#12-ptz-presets-e-tour)  
13. [Talk-back (áudio)](#13-talk-back-áudio)  
14. [Eventos, ack e automação](#14-eventos-ack-e-automação)  
15. [Mosaicos e layouts](#15-mosaicos-e-layouts)  
16. [Playback multi-câmera e pop-out](#16-playback-multi-câmera-e-pop-out)  
17. [Mapa sinóptico](#17-mapa-sinóptico)  
18. [Saúde das câmeras e alertas](#18-saúde-das-câmeras-e-alertas)  
19. [Criptografar gravações em repouso](#19-criptografar-gravações-em-repouso)  
20. [Retenção, cotas e log de purge (LGPD)](#20-retenção-cotas-e-log-de-purge-lgpd)  
21. [Archive frio (NAS)](#21-archive-frio-nas)  
22. [Multi-volume de disco](#22-multi-volume-de-disco)  
23. [Sharding e HA do gravador](#23-sharding-e-ha-do-gravador)  
24. [Event bus Redis (multi-nó)](#24-event-bus-redis-multi-nó)  
25. [Métricas Prometheus](#25-métricas-prometheus)  
26. [Drivers e maturidade](#26-drivers-e-maturidade)  
27. [Permissões por câmera](#27-permissões-por-câmera)  
28. [Atalhos do posto](#28-atalhos-do-posto)  
29. [Checklist de validação](#29-checklist-de-validação)  
30. [Problemas comuns](#30-problemas-comuns)

---

## Convenções deste manual

| Símbolo | Significado |
|---------|-------------|
| **Posto** | Tela `monitor.html` |
| **Admin** | `admin.html` ou aba Config |
| **API** | Requisição autenticada com JWT (`Authorization: Bearer …`) |
| 🟢 | Disponível pela interface |
| ⚙️ | Exige configuração / API |

Obter token (PowerShell):

```powershell
$r = Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/auth/login `
  -ContentType 'application/json' `
  -Body '{"username":"admin","password":"SUA_SENHA"}'
$token = $r.token
$h = @{ Authorization = "Bearer $token" }
```

---

## 1. Primeiro acesso e login

### Situação
Instalação nova; entrar no sistema pela primeira vez.

### Como fazer
1. Suba a API (ex.: `start-windows.ps1` ou `dotnet run` no projeto Api).  
2. Confirme o MediaMTX e o FFmpeg no PATH.  
3. Abra `http://localhost:8080/`.  
4. Use o usuário administrador criado no bootstrap (senha em `Security:BootstrapAdminPassword` ou a gerada no log no primeiro boot).  
5. Após login, o sistema redireciona para o **posto** (`/monitor.html`).

### Dicas
- Portal com `?stay=1` mantém o launcher.  
- SSO OIDC/SAML, se habilitado, também cai no posto.

---

## 2. Cadastrar e testar câmera

### Situação
Integrador precisa incluir uma câmera nova.

### Como fazer (Admin)
1. Abra **Config** (admin) → **Câmeras**.  
2. Preencha: nome, driver (`hikvision`, `onvif`, `dahua`, …), IP, porta, usuário e senha.  
3. Opcional: URL RTSP fixa em `StreamUrl` (ignora montagem automática).  
4. Use **Testar conexão** (quando disponível) ou salve e confira status **Online**.  
5. Ajuste modo de gravação, retenção (dias), áudio, pré-alarme e edge-pull se necessário.

### Como fazer (API)
```http
POST /api/vms/cameras
Authorization: Bearer {token}
Content-Type: application/json

{
  "name": "Portaria",
  "host": "192.168.1.50",
  "driver": "hikvision",
  "port": 80,
  "username": "admin",
  "password": "****",
  "recording": "Continuous",
  "retentionDays": 7,
  "eventRecordSeconds": 60,
  "preEventSeconds": 15,
  "recordAudio": true,
  "edgePullEnabled": false
}
```

### Resultado esperado
- Câmera na lista do posto.  
- Path `cam{id}` no MediaMTX.  
- Se licença de canais esgotada → HTTP **409**.

### Permissão
`camera.config`

---

## 3. Ver ao vivo (WebRTC/HLS)

### Situação
Operador quer monitorar câmeras em mosaico.

### Como fazer (Posto)
1. Login → aba **Live**.  
2. Na lista à esquerda, **arraste** a câmera para um quadrante (ou duplo clique / ▶).  
3. Escolha o layout (1×1, 2×2, …) na barra superior.  
4. Botão **Stream: SUB/MAIN** — sub = grid leve; main = detalhe.  
5. Por quadrante: **MAIN/SUB**, reconectar ↻, foto 📷, congelar ⏸.

### Comportamento técnico
- A plataforma mantém **1 pull RTSP** na câmera (via MediaMTX).  
- Live e gravação leem do gateway.  
- H.265 pode usar transcoder se `Vms:TranscodeLive=true` (gasta CPU; em produção costuma ficar `false`).

### API
```http
GET /api/vms/cameras/{id}/stream?quality=sub
```
Resposta: URLs HLS e WebRTC (+ flag `ready`).

### Permissão
`camera.view`

---

## 4. Gravação contínua

### Situação
Gravar 24×7 (ou só nas janelas da agenda).

### Como fazer
1. Cadastro da câmera: **Recording = Continuous**.  
2. Opcional: agenda (ver §6).  
3. Confira pastas: `{StoragePath}/{deviceId}/c_yyyyMMdd_HHmmss.mp4`.  
4. No posto: HUD **REC** / health após alguns minutos.

### Config relevante (`appsettings` → `Vms`)
| Chave | Função |
|-------|--------|
| `SegmentSeconds` | Duração de cada arquivo (padrão 600 s) |
| `RecordAudio` | Áudio global |
| `RecordBrowserCompatible` | H.264 + AAC legível no browser |
| `RecordFromMediaGateway` | Grava do MediaMTX (recomendado) |
| `SingleCameraRtspPull` | Só 1 sessão na câmera |

### Permissão
Configuração: `camera.config`. Playback: `camera.playback`.

---

## 5. Gravação por evento + pré-alarme

### Situação
Economizar disco: gravar motion/alarme e **alguns segundos antes** do evento.

### Como fazer
1. Câmera: **Recording = OnEvent**.  
2. `EventRecordSeconds` = quanto tempo continua após o último evento (ex.: 60).  
3. `PreEventSeconds` = pré-alarme (ex.: 15). **0** = desliga o ring buffer.  
4. Global: `Vms:PreEventSeconds` (padrão se device herdar).

### O que acontece no disco
| Prefixo | Significado |
|---------|-------------|
| `p_*.mp4` | Pré-buffer (ring); apagado se passar do tempo sem evento |
| `e_*.mp4` | Gravação de evento |
| `c_*.mp4` | Contínuo |

Quando chega evento (motion etc.):
1. Segmentos `p_*` recentes viram **event + Protected**.  
2. Gravação segue em modo evento até o silêncio (`EventRecordSeconds`).

### Como validar
1. Force um motion na câmera (ou `POST /api/vms/events`).  
2. Veja novos arquivos e timeline com trigger `event` / `prebuffer`.  
3. Métrica `vms_preevent_promotions_total` sobe.

---

## 6. Agendar janelas de gravação

### Situação
Gravar só em horário comercial, ou só motion à noite.

### Como fazer
No **Admin**, cadastre faixas `ScheduleSlot` por câmera:
- **Kind = Recording** → governa modo Continuous.  
- **Kind = Event** → governa se OnEvent pode gravar naquele horário.  
- `Day` nulo = todos os dias; `Start`/`End` em HH:mm (pode cruzar meia-noite).

Sem faixas = 24×7.

### Comportamento
Fora da agenda o FFmpeg **não** sobe (contínuo) ou **ignora** motion (evento).

---

## 7. Playback e linha do tempo

### Situação
Rever o que aconteceu em um dia.

### Como fazer (Posto)
1. Selecione a câmera no grid **ou** use 📼 / tecla **R**.  
2. Abre o diálogo de **Gravações**.  
3. Calendário: dias com gravação.  
4. Barra 24 h:  
   - **Azul** = contínuo  
   - **Amarelo** = evento  
   - **Hachurado vermelho** = buraco (gap)  
   - Miniaturas no topo da barra (se já geradas)  
5. Clique no segmento → play. Espaço = play/pause; roda do mouse = zoom na timeline.

### API
```http
GET /api/vms/cameras/{id}/timeline?from=2026-07-24T00:00:00Z&to=2026-07-25T00:00:00Z
GET /api/vms/cameras/{id}/recordings?from=…&to=…&page=1&pageSize=50
GET /api/vms/recordings/{recordingId}/file
GET /api/vms/cameras/{id}/thumbs/{yyyyMMdd_HHmm}
```

A timeline devolve: `blocos`, `segmentos`, `gaps`, `thumbs`, `bookmarks`, `eventos`.

### Permissão
`camera.playback`

---

## 8. Busca smart (eventos + gravação)

### Situação
Achar motion/intrusão e o trecho de vídeo mais próximo.

### Como fazer (API)
```http
GET /api/vms/cameras/{id}/search?type=motion&from=…&to=…&take=100
```

Atalhos de `type`: `motion` / `movimento` agregam vários tipos de movimento.

### Resultado
Lista de hits com `eventId`, horário, `recordingId` associado.

---

## 9. Bookmarks (proteger prova)

### Situação
Marcar incidente para a retenção **não apagar**.

### Como fazer (Posto)
- Live: atalho de bookmark (±30 s) quando disponível.  
- Ou API:

```http
POST /api/vms/cameras/{id}/bookmarks
{
  "title": "Furto portaria",
  "from": "2026-07-24T14:00:00Z",
  "to": "2026-07-24T14:15:00Z",
  "description": "POP #123"
}
```

### Efeito
Gravações cobertas pelo intervalo ficam `Protected = true` e **não** entram na purga automática.

### Remover
```http
DELETE /api/vms/bookmarks/{id}
```

---

## 10. Exportar vídeo com prova de integridade

### Situação
Entregar MP4 para investigação / polícia com hash e assinatura.

### Como fazer (Posto)
1. Aba **Export** (ou página embutida).  
2. Escolha câmera, início e fim.  
3. Exporte → baixa MP4.

### Como fazer (API)
```http
POST /api/vms/cameras/{id}/export
{
  "from": "2026-07-24T14:00:00Z",
  "to": "2026-07-24T14:10:00Z"
}
```

Headers da resposta (quando ok):
| Header | Conteúdo |
|--------|----------|
| `X-Export-Bytes` | Tamanho |
| `X-Export-Sha256` | Hash SHA-256 do arquivo |
| `X-Export-Signature` | HMAC-SHA256 (se chave configurada) |

Arquivo em disco: `{ExportPath}/cam{id}_….mp4` + opcional `.sig`.

### Opções de sistema (Admin → Configurações)
| Opção | Efeito no export |
|-------|------------------|
| Watermark export | Marca d’água com usuário/data |
| Blur faces on export | Boxblur global (LGPD simplificado) |
| Encrypt recordings | (gravação em repouso; export decifra) |
| Privacy masks da câmera | Caixas pretas nas ROIs no export |

### Listar cadeia de custódia
```http
GET /api/vms/export/records?take=50
```

### Verificar integridade (admin)
```http
POST /api/vms/export/verify
{
  "exportId": 12
}
```
ou
```json
{ "filePath": "C:\\…\\exports\\cam3_….mp4" }
```

Resposta: `sha256`, `shaMatch`, `signatureValid`.

### Limites
- `Vms:MaxExportMinutes` (padrão 60).  
- Permissão: `camera.export`.

---

## 11. Máscara de privacidade

### Situação
Cobrir área sensível (caixa, biometria, monitores) no **live** e no **export**.

### Como fazer rápido (Posto)
1. Coloque a câmera no grid e selecione o quadrante.  
2. **Mais ▾ → Máscara privacidade…**  
3. Aplica retângulo central de teste; overlay **PRIV** preto no live.

### Como fazer (API) — polígono customizado
Coordenadas **normalizadas 0–1** (canto superior esquerdo = 0,0):

```http
PUT /api/vms/cameras/{id}/privacy-masks
{
  "name": "caixa",
  "enabled": true,
  "polygonsJson": "[{\"points\":[[0.1,0.2],[0.4,0.2],[0.4,0.5],[0.1,0.5]]}]"
}
```

Consultar:
```http
GET /api/vms/cameras/{id}/privacy-masks
```

### Efeitos
| Onde | Comportamento |
|------|----------------|
| Live | Polígonos pretos no SVG do quadrante |
| Export | FFmpeg `drawbox` nas bounding boxes |

### Permissão
Leitura: `camera.view` · Escrita: `camera.config`

---

## 12. PTZ, presets e tour

### Situação
Controlar dome/PTZ.

### Como fazer (Posto)
1. Selecione a câmera no grid.  
2. Painel PTZ: setas, zoom, sensibilidade.  
3. Teclado: setas / +/-.  
4. Presets: salvar / ir.  
5. Tour: ▶ / ⏹ na UI (quando exposto).

### API
```http
POST /api/vms/cameras/{id}/ptz/move
{ "pan": 0.5, "tilt": -0.2, "zoom": 0, "timeoutSeconds": 2 }

POST /api/vms/cameras/{id}/ptz/stop

GET  /api/vms/cameras/{id}/ptz/presets
PUT  /api/vms/cameras/{id}/ptz/presets/{n}
POST /api/vms/cameras/{id}/ptz/tour/start
POST /api/vms/cameras/{id}/ptz/tour/stop
```

Velocidade **normalizada −1…1** (cada driver converte para a escala do fabricante).

### Permissão
`camera.ptz` (listar presets pode ser `camera.view`)

### Drivers
Hikvision / ONVIF / Dahua costumam ter PTZ; Uniview/Bosch/Samsung podem estar **experimental**.

---

## 13. Talk-back (áudio)

### Situação
Falar com o local pela câmera.

### Como fazer (Posto)
1. Selecione a câmera.  
2. Segure o botão **Talk-back** (pressionar para falar).  
3. Solte para parar.

### API
```http
POST /api/vms/cameras/{id}/talk/open
POST /api/vms/cameras/{id}/talk   { "base64": "…" }
POST /api/vms/cameras/{id}/talk/close
```

### Observação
Depende do driver/fabricante; se falhar, confira permissões e suporte do equipamento.

---

## 14. Eventos, ack e automação

### Situação
Tratar alarmes e reagir automaticamente.

### Como fazer (Posto)
1. Aba **Eventos**.  
2. Fila: filtrar, **ack**, live da câmera.  
3. Botões de ação cadastrados no admin (confirmar, relé, etc.).

### API
```http
GET  /api/vms/events?unacknowledged=true
POST /api/vms/events/{id}/ack
POST /api/vms/events
{ "type": "motion", "deviceId": 3, "severity": 2, "payload": "{}" }

GET  /api/vms/event-action-buttons
POST /api/vms/events/{eventId}/actions/{buttonId}
```

### Automação (Admin)
Regras IFTTT: **SE** tipo de evento / câmera / severidade **ENTÃO**:
- E-mail, PTZ preset, Bookmark, HTTP, relé  
- Ações de cliente: popup, som, abrir live/playback/mapa  

Campos úteis: agenda horária, cooldown (anti-flood).

### WebSocket tempo real
```
ws://localhost:8080/ws/events?access_token={jwt}
```

---

## 15. Mosaicos e layouts

### Situação
Salvar disposição de câmeras do operador.

### Como fazer (Posto)
1. Monte o grid.  
2. **Salvar** mosaico (nome).  
3. **Carregar** pelo seletor.  
4. Layouts do servidor: API `/api/vms/layouts` (por usuário).

### Atalhos
- `Ctrl+S` salvar mosaico  
- `Ctrl+1…7` layouts rápidos  

---

## 16. Playback multi-câmera e pop-out

### Situação
Investigar várias câmeras no mesmo horário; usar 2º monitor.

### Multi-cam sync (Posto)
1. Coloque 2+ câmeras no stage.  
2. Clique **Sync multi**.  
3. Abre playback da primeira + barra com as demais e players auxiliares.  
4. **▶/❚❚ todos** sincroniza play/pause dos vídeos do diálogo.

### Pop-out (Posto)
1. **Mais ▾ → Pop-out stage**.  
2. Abre janela listando as câmeras do grid (útil no 2º monitor; live pleno permanece no posto principal / F11).

---

## 17. Mapa sinóptico

### Situação
Localizar câmera no planta/mapa e abrir live.

### Como fazer (Posto)
1. Aba **Mapa**.  
2. Selecione o mapa.  
3. Clique / duplo clique no marcador → live ou playback.

### Admin
Criar mapa, upload de fundo, posicionar marcadores (edição rica ainda no admin).

### API
Grupo `/api/vms/maps` (CRUD, background, markers).

---

## 18. Saúde das câmeras e alertas

### Situação
Saber o que está offline ou parou de gravar.

### Como fazer (Posto)
- HUD no quadrante: REC / FPS / bitrate (poll de health).  
- Eventos: `device_offline`, `device_online`, `recording_stalled`, `recording_gap`, `media_gateway_down/up`.

### API
```http
GET /api/vms/cameras/health
GET /api/vms/cameras/{id}/health
```

### Config
| Chave | Função |
|-------|--------|
| `SilentRecordingMinutes` | Tempo sem segmento → `recording_stalled` |
| `GapAlertMinutes` | Buraco entre segmentos → `recording_gap` |

---

## 19. Criptografar gravações em repouso

### Situação
Arquivos no disco não podem ficar em claro.

### Como fazer
1. Admin → **Configurações do sistema**.  
2. Ative **Encrypt recordings**.  
3. Segmentos fechados viram `.mp4.enc` (AES-256-GCM streaming).  
4. Playback/export decifram sob demanda (cache `.plain.cache`).

### ⚠️ Atenção
- A chave vem do **Data Protection keyring** (`Security:KeyRingPath`).  
- **Backup do keyring é obrigatório** — perda = gravações irrecuperáveis.  
- Em multi-nó HA, o keyring deve ser o **mesmo volume** em todos os processos.

---

## 20. Retenção, cotas e log de purge (LGPD)

### Situação
Descartar o antigo e provar o que foi apagado.

### Como funciona (ordem)
1. Prazo por câmera: `RetentionDays`.  
2. Cota por câmera: `MaxStorageGb` (0 = sem limite).  
3. Cota global: `Vms:MaxStorageGb`.  
4. **Nunca** apaga `Protected` (bookmark).

Pré-buffer (`p_*`) antigo é limpo automaticamente (motivo `prebuffer`).

### Consultar purge (admin)
```http
GET /api/vms/retention/purge-log?deviceId=3&take=100
```

Campos: path, tamanho, `reason` (`retention_days` | `camera_quota` | `global_quota` | `prebuffer` | …), data.

---

## 21. Archive frio (NAS)

### Situação
Manter gravações antigas em pasta barata sem apagar ainda.

### Como fazer
Admin → Sistema:
- `ArchivePath` = pasta do NAS (absoluta ou relativa).  
- `ArchiveAfterDays` = dias em storage quente (ex.: 14). **0** = desliga.

O serviço move periodicamente segmentos **não protegidos** e atualiza o path no banco.

---

## 22. Multi-volume de disco

### Situação
Dois discos; gravar onde há mais espaço.

### Como fazer (`appsettings.json`)
```json
"Vms": {
  "StoragePath": "./data/recordings",
  "StorageVolumes": [
    "D:/vms-vol2",
    "E:/vms-vol3"
  ]
}
```

Novos segmentos escolhem o volume com **mais espaço livre**. A retenção indexa em todos.

---

## 23. Sharding e HA do gravador

### Situação
Vários nós; cada um grava um subconjunto de câmeras sem duplicar.

### Sharding
```json
"Vms": {
  "ShardIndex": 0,
  "ShardCount": 2
}
```
Nó grava câmeras com `Id % ShardCount == ShardIndex`.

### HA (lease)
```json
"HaEnabled": true,
"LeaseSeconds": 30,
"NodeId": "gravador-a"
```
- Mesmo **banco** e mesmo **keyring**.  
- Só o detentor do lease grava a câmera.

---

## 24. Event bus Redis (multi-nó)

### Situação
Dois processos API: eventos e automação precisam ser compartilhados.

### Como fazer
```json
"Vms": {
  "EventBus": "redis://localhost:6379",
  "NodeId": "api-1"
}
```

Formatos aceitos:
- `redis://host:6379`  
- `rediss://:senha@host:6380` (TLS)  
- `host:6379,password=…,abortConnect=false`

### Comportamento
- Canal: `sp:events`  
- Vazio = **in-memory** (All-in-One)  
- Redis inacessível no boot → log de erro + fan-out **só local** (API sobe)

---

## 25. Métricas Prometheus

### Situação
Monitorar saúde no Grafana/Prometheus.

### Como fazer
```http
GET http://localhost:8080/metrics
```

### Métricas VMS úteis
| Métrica | Significado |
|---------|-------------|
| `vms_recording_active` | FFmpeg gravando neste nó |
| `vms_cameras_online` / `offline` | Último health |
| `vms_media_gateway_up` | MediaMTX ok (1/0) |
| `vms_exports_total` | Exports |
| `vms_export_duration_ms_total` | Tempo de export |
| `vms_segments_indexed_total` | Segmentos indexados |
| `vms_purge_total` | Apagados pela retenção |
| `vms_recording_gaps_total` | Buracos detectados |
| `vms_preevent_promotions_total` | Pré-buffer → evento |
| `vms_thumbnails_total` | Miniaturas geradas |
| `vms_archive_moved_total` | Movidos para archive |

Restrinja `/metrics` no proxy em produção.

---

## 26. Drivers e maturidade

### Situação
Saber se o fabricante está certificado ou experimental.

### Como fazer
```http
GET /api/drivers
Authorization: Bearer {token}   # admin
```

Campos: `name`, `supports`, `maturity` (`certified` | `supported` | `partial` | `experimental`), `stream`, `ptz`, `events`, `note`.

| Exemplos | Maturidade típica |
|----------|-------------------|
| hikvision, onvif | certified |
| dahua, intelbras | supported |
| axis | partial |
| uniview, bosch, samsung | experimental |

---

## 27. Permissões por câmera

### Situação
Operador A vê só o bloco A; export só para supervisores.

### Direitos principais
| Permissão | Uso |
|-----------|-----|
| `camera.view` | Live, lista, snapshot |
| `camera.playback` | Timeline, arquivo |
| `camera.export` | Export MP4 |
| `camera.ptz` | PTZ |
| `camera.config` | Cadastro / máscara |
| `event.ack` | Tratar eventos |

### Como fazer
Admin → Grupos / Direitos: **Allow** ou **Deny** por usuário/grupo e câmera (ou “todas”).  
**Deny sempre vence Allow.**

Detalhes: [PERMISSOES-E-GRUPOS.md](PERMISSOES-E-GRUPOS.md).

---

## 28. Atalhos do posto

| Tecla | Ação |
|-------|------|
| `R` | Playback da câmera selecionada |
| `I` | Instant replay ~60 s (quando disponível) |
| `B` | Bookmark rápido (quando disponível) |
| `L` | Reconectar live |
| `Q` | Alternar MAIN/SUB no quadrante |
| Setas / +/- | PTZ |
| `F5` | Atualizar câmeras |
| `F11` | Tela cheia |
| `Delete` | Remover câmera do grid |
| `Ctrl+S` | Salvar mosaico |
| `Ctrl+E` | Servidores |
| `Ctrl+1…7` | Layouts |
| `Esc` | Fechar diálogos / desmaximizar |

Deep-link:
```
/monitor.html?view=events
/monitor.html?view=map
/monitor.html?cam=3&action=playback
```

---

## 29. Checklist de validação

Use após instalação ou upgrade:

1. [ ] Login → cai em `/monitor.html`  
2. [ ] Câmera cadastrada e **Online**  
3. [ ] Live no grid (HLS ou WebRTC)  
4. [ ] Arquivo `c_*.mp4` ou `e_*.mp4` / `p_*.mp4` no storage  
5. [ ] Timeline mostra segmentos; gaps hachurados se houver buraco  
6. [ ] Bookmark protege trecho  
7. [ ] Export baixa MP4 com `X-Export-Sha256`  
8. [ ] `POST /export/verify` retorna `signatureValid` / `shaMatch`  
9. [ ] Máscara aparece no live (PRIV)  
10. [ ] `GET /metrics` lista `vms_*`  
11. [ ] Existe `{StoragePath}/cluster.uuid`  
12. [ ] (Opcional) Redis: dois nós recebem o mesmo evento  

Testes automatizados:
```bash
dotnet test tests/SecurityPlatform.Tests
```

---

## 30. Problemas comuns

| Sintoma | O que verificar |
|---------|-----------------|
| Live “sem sinal” | MediaMTX rodando? `GET /metrics` → `vms_media_gateway_up`? Evento `media_gateway_down`? |
| Não grava | `RecorderEnabled`? Agenda? Disco cheio? Path MediaMTX ready? |
| OnEvent sem pré-alarme | `PreEventSeconds` > 0? Agenda Event ativa? |
| Export minúsculo / erro | Intervalo sem segmentos indexados; espere o Retention indexar (~1 min) |
| Playback 404 | Path no banco vs `StoragePath`; cluster/volume errado |
| Boot falha em cluster.uuid | Dois ambientes no mesmo volume — separe pastas ou alinhe `Vms:ClusterId` |
| CPU alta | `TranscodeLive=false`; menos reencode H.265 |
| PTZ falha | Driver experimental? Permissão `camera.ptz`? |
| Export sem assinatura | `Security:JwtKey` ou `LicenseSigningKey` com ≥ 16 chars |
| Máscara não aparece | Permissão view? PUT com `camera.config`? Polígono ≥ 3 pontos? |

---

## Apêndice A — Estrutura de pastas de dados

```
data/
  platform.db              # SQLite (dev)
  keys/                    # Data Protection (senhas + cifra de gravação)
  recordings/              # Storage quente
    cluster.uuid
    {deviceId}/
      c_….mp4 | e_….mp4 | p_….mp4 | edge_….mp4
      ….mp4.enc            # se EncryptRecordings
    _thumbs/{deviceId}/    # miniaturas timeline
  exports/                 # MP4 exportados + .sig
  faces/                   # galeria facial (se usado)
```

---

## Apêndice B — Config `Vms` resumida

| Chave | Padrão | Uso |
|-------|--------|-----|
| `MediaMtxApi` | `http://localhost:9997` | API MediaMTX |
| `MediaPublicHost` | `http://localhost` | Host visto pelo browser |
| `StoragePath` | `./data/recordings` | Gravações |
| `ExportPath` | `./data/exports` | Exports |
| `FfmpegPath` | `ffmpeg` | Binário |
| `SegmentSeconds` | `600` | Duração do segmento |
| `PreEventSeconds` | `15` | Pré-alarme global |
| `MaxExportMinutes` | `60` | Teto export |
| `MaxStorageGb` | `0` | Cota global (0=∞) |
| `TranscodeLive` | `false` | H.265→H.264 live |
| `HaEnabled` | `false` | Lease multi-nó |
| `ShardIndex` / `ShardCount` | `0` / `1` | Fatia de câmeras |
| `ClusterId` | `""` | Lock de volume |
| `StorageVolumes` | `[]` | Discos extras |
| `EventBus` | `""` | Redis ou vazio |
| `ThumbnailIntervalMinutes` | `10` | Job de thumbs (0=off) |
| `GapAlertMinutes` | `5` | Alarme de buraco |
| `SilentRecordingMinutes` | `15` | Stall de gravação |

---

## Apêndice C — Mapa rápido UI × API

| Quero… | Posto | API |
|--------|-------|-----|
| Ver live | Arrastar câmera | `GET …/stream` |
| Playback | R / 📼 | `…/timeline`, `…/file` |
| Exportar | Aba Export | `POST …/export` |
| Provar export | — | `POST …/export/verify` |
| Máscara | Mais → Máscara… | `PUT …/privacy-masks` |
| PTZ | Painel / teclado | `POST …/ptz/move` |
| Eventos | Aba Eventos | `GET …/events` |
| Saúde | HUD | `GET …/cameras/health` |
| Métricas | — | `GET /metrics` |

---

*Manual alinhado às funcionalidades implementadas em 2026-07-24. Para decisões de arquitetura e gaps, ver [VMS-AUDITORIA-QUALIDADE.md](VMS-AUDITORIA-QUALIDADE.md). Para go-live de infraestrutura, ver [OPS-VMS.md](OPS-VMS.md).*
