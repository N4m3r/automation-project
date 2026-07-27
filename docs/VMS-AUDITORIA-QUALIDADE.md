# Auditoria de Qualidade — Módulo VMS

> **Data:** 2026-07-24  
> **Escopo:** inventário do que existe, o que precisa melhorar e o que falta para um VMS de qualidade comercial (nível Digifort / Milestone / Genetec light).  
> **Código:** `src/SecurityPlatform.Modules.Vms/` · posto `wwwroot/monitor.html` · drivers em `src/SecurityPlatform.Drivers.*`  
> **Documentos relacionados:** [MANUAL-VMS-FUNCIONALIDADES.md](MANUAL-VMS-FUNCIONALIDADES.md) · [MODULO-VMS.md](MODULO-VMS.md) · [OPS-VMS.md](OPS-VMS.md) · [pendente.md](../pendente.md)

---

## 1. Resumo executivo

A plataforma já tem um **núcleo de VMS funcional e bem pensado** para All-in-One / multi-nó com sharding: live (WebRTC/HLS via MediaMTX), gravação FFmpeg com segmentos, retenção LGPD, export com watermark/assinatura, PTZ, timeline, bookmarks, saúde por câmera, edge-pull, HA por lease, automação e posto unificado.

O gap para “módulo de VMS de **qualidade**” não é mais “existe gravação?”, e sim **robustez de produção, paridade de features com o mercado, UX de operação 24/7 e cobertura de testes de ponta a ponta**.

| Dimensão | Nota (0–5) | Leitura |
|----------|:----------:|---------|
| Live / gateway de mídia | **4** | MediaMTX + 1 pull RTSP + WebRTC/HLS + transcode H.265→H.264 |
| Gravação e retenção | **4** | Contínuo / evento / agenda; cotas; protect; edge-pull; crypto em repouso |
| Playback / export | **3,5** | Timeline 24h, Range, export MP4, watermark, blur, .sig — falta multi-cam sync e thumbs |
| Drivers / multi-marca | **3,5** | Hikvision forte; ONVIF com PTZ; Dahua eventos; demais vendors leves |
| Eventos / automação | **3,5** | Barramento, WS, regras IFTTT, botões de ação; sem correlação avançada |
| Posto de operação (UX) | **3,5** | `monitor.html` rico (~4k linhas) monólito vanilla; posto unificado OK |
| HA / escala | **3** | Sharding + lease; EventBus ainda in-memory; storage único por nó |
| Segurança / LGPD | **3,5** | Permissões por câmera, crypto, watermark, blur global (não ML) |
| Observabilidade | **2,5** | Logs + health HUD; falta métricas Prometheus/OpenTelemetry de mídia |
| Testes | **2** | Unitários de parsing/permissão/schedule; quase zero E2E de gravação/live |
| Analytics / IA | **1,5** | Face fingerprint leve + stub IA; LPR/plates cadastro; sem motor real |

**Maturidade global estimada: ~3,3 / 5** — bom MVP avançado / beta operacional; ainda não “enterprise VMS fechado”.

---

## 2. Inventário do que já existe

### 2.1 Serviços em segundo plano (backend)

| Serviço | Papel | Qualidade atual |
|---------|-------|-----------------|
| `MediaSyncService` | Paths MediaMTX ↔ banco | Bom; cache local + revalidação pós-restart |
| `MediaGateway` | Pull RTSP único, sub on-demand, paths `camN` / `camNs` / `camNtc` | Bom — decisão correta de 1 sessão na câmera |
| `RecorderService` | FFmpeg por câmera; Continuous / OnEvent; agenda; stats FPS/bitrate | Bom; OnEvent reage ao barramento (não espera 15 s) |
| `RetentionService` | Indexa segmentos, prazo, cota cam/global, protect, encrypt | Bom; risco documentado se 2 bancos no mesmo volume |
| `CameraHealthService` | Online/offline + `recording_stalled` | Bom para operação |
| `DeviceEventListener` | Eventos nativos por driver (sharding) | Depende da profundidade do driver |
| `EdgePullService` | Preenche buracos via RTSP playback (Hik/Dahua) | Parcial — “Profile G light” |
| `LiveTranscodeService` | H.265→H.264 no MediaMTX | Opcional, CPU-heavy |
| `RecorderLeaseService` | HA: lease no DB | OK com mesmo DB + keyring compartilhado |
| `AutomationEngine` + `EventActionRunner` | SE evento → ações (e-mail, PTZ, bookmark, HTTP, cliente) | Bom para POP básico |
| `PtzTourService` | Patrulha de presets | OK |
| `SiaReceiverService` / `MqttBridgeService` | Alarme IP / IoT | Simplificados (fora do core vídeo, mas no módulo) |
| `RecordingNormalizer` / `RecordingCrypto` / `RecordingExporter` / `ExportSigner` | Playback web, cifra AES-GCM, export, HMAC | Funcionais |

### 2.2 API principal (`/api/vms`)

- CRUD câmeras + grupos; stream; snapshot; talk-back  
- PTZ move/stop/presets/tour  
- Recordings paginados, timeline, recording-days, search  
- Export; bookmarks; layouts de monitor  
- Health por câmera / agregado  
- Eventos + ack + botões de ação  
- Mapas sinópticos, acesso, alarmes, analytics/LPR, face search (no mesmo assembly)

### 2.3 Drivers

| Driver | Stream | PTZ | Eventos | Maturidade |
|--------|:------:|:---:|:-------:|------------|
| **Hikvision** (ISAPI) | ✅ | ✅ | ✅ rich | Alta |
| **ONVIF** | ✅ | ✅ (SOAP) | parcial | Média-alta |
| **Dahua** | ✅ | ✅ HTTP | ✅ eventManager | Média-alta |
| **Intelbras** | via Dahua | ✅ | ✅ | Média |
| **Axis** | ✅ | parcial | parcial | Média |
| **Uniview / Bosch / Samsung** | stream + básico | limitado | heartbeat | Baixa-média |
| **HttpIo / Commbox** | I/O | — | — | SCA/I/O |

### 2.4 Cliente de operação

- **`monitor.html`**: posto unificado (Live, Eventos, Mapa, Acesso, Export, Config iframe)  
- Multi-servidor no browser, mosaicos, WebRTC→HLS fallback, PTZ teclado, tour, bookmark, replay 60 s, health HUD, alarme sonoro  
- Playback com timeline 24 h, zoom, segmentos contínuos/evento  
- **Admin** em `admin.html` (iframe no posto)

### 2.5 Configuração relevante (`Vms` + `SystemSettings`)

- Sharding, HA lease, single RTSP pull, record from gateway  
- Segmentos, cotas, áudio, browser-compatible H.264, transcode live  
- `EncryptRecordings`, watermark export, blur faces (boxblur), SMTP  

---

## 3. O que está bem resolvido (não reescrever)

1. **Arquitetura de 1 pull RTSP na câmera** (`SingleCameraRtspPull` + gravação do gateway) — evita esgotar sessões Hikvision.  
2. **OnEvent ligado ao EventBus** com latência baixa e anti-flood de start.  
3. **Retenção com protect + cotas + StartedAt do nome do arquivo** (não `CreationTime`).  
4. **Permissões por câmera** (allow/deny) em listagem e endpoints.  
5. **Export com `-c copy` quando possível**, watermark, assinatura `.sig`, blur LGPD opcional.  
6. **Criptografia em repouso** (`RecordingCrypto` AES-256-GCM + Data Protection) acionável por `EncryptRecordings`.  
7. **Sharding + lease HA** como modelo de escala horizontal simples.  
8. **Posto unificado** com deep-links e fluxos de operador reais.  
9. **Normalização de gravação** para playback no browser (HEVC → H.264).  

Estes pontos já estão no nível “produto sério”. Prioridade deve ser **fechar gaps de qualidade e features de mercado**, não redesenhar o core.

---

## 4. O que precisa melhorar (qualidade, não feature nova)

### 4.1 Crítico para produção

| # | Problema | Impacto | Direção de solução |
|---|----------|---------|-------------------|
| P1 | **EventBus in-memory** | Multi-nó não compartilha eventos/automação/WS entre processos | Redis / NATS / Rabbit com fan-out; WS no nó de API com bus distribuído |
| P2 | **Testes sem E2E de mídia** | Regressão em gravação/export/live passa despercebida | Suite com FFmpeg lavfi + MediaMTX em CI; golden files de segmento |
| P3 | **Cifra carrega arquivo inteiro em RAM** (`File.ReadAllBytes`) | Segmentos longos / 4K estouram memória | Stream AES-GCM em chunks + encrypt no flush do segmento |
| P4 | **Decrypt cria temp em disco em todo Range de playback** | I/O e latência altos sob muitos operadores | Cache de plain com TTL, ou range decrypt se formato permitir |
| P5 | **`monitor.html` monólito ~4k linhas** | Manutenção, bugs de estado, difícil testar UI | Modularizar (ES modules / build leve); separar Live / Playback / Events |
| P6 | **Dependência rígida do MediaMTX local** | Live some se o processo morrer; recovery manual | Health + auto-restart (Windows service / supervisord); alerta `media_gateway_down` |
| P7 | **Observabilidade fraca de mídia** | Não se mede “câmeras gravando”, “latência WHEP”, “segmentos/min” | Métricas Prometheus (já há esboço PlatformMetrics) + painel no admin |
| P8 | **Risco de retenção com 2 bancos no mesmo volume** | Já documentado, mas sem trava técnica | Lock de volume (arquivo lease) ou UUID de cluster no root de storage |

### 4.2 Importante (qualidade operacional)

| # | Problema | Direção |
|---|----------|---------|
| Q1 | **Pre-buffer / pre-event ausente** no OnEvent | Ring buffer (ex.: 10–30 s) em RAM ou segmento rolling — padrão de mercado |
| Q2 | **Timeline sem thumbnails / smart search visual** | Miniaturas a cada N min; indexar motion score no segmento |
| Q3 | **Playback multi-câmera sincronizado** | Clock mestre no cliente + N players com offset |
| Q4 | **Buracos de gravação** só parcialmente cobertos pelo edge-pull | Relatório de gaps na UI; política “critical gap → alarme” |
| Q5 | **EndedAt / duração de segmento** às vezes estimada | ffprobe no indexador; gravar duração real no banco |
| Q6 | **Talk-back / áudio bidirecional** frágil por fabricante | Contrato de teste por driver; fallback desabilitado explícito |
| Q7 | **Transcode live sempre on** no appsettings de dev (`TranscodeLive: true`) | Default false em prod; perfil por edição de licença |
| Q8 | **StoragePaths / Unicode no Windows** | Já mitigado no export (workDir ASCII); generalizar para todo path FFmpeg |
| Q9 | **Face search = fingerprint 64×64** | Documentar como “aproximação”; não vender como biometria forense |
| Q10 | **Blur LGPD = boxblur global** | Não anonimiza só faces; UX deve deixar isso claro; roadmap ML opcional |
| Q11 | **Admin em iframe** | Config nativa no posto ou SPA admin compartilhando auth |
| Q12 | **Sem policy de storage multi-volume** | Spool A/B, quota por volume, failover de path |

### 4.3 Dívida de código / arquitetura

| Item | Nota |
|------|------|
| VMS assembly mistura SCA + Alarmes + Maps + Face | Separar bounded contexts (`Modules.Access`, `Modules.Alarms`) ou pastas claras + ownership |
| `VendorDrivers.cs` monolítico | Um arquivo por fabricante; testes de contrato `IDeviceDriver` |
| Migrations SQLite + Postgres duplicadas | Aceitável; garantir checklist de migration em todo schema change |
| Secrets de câmera | Protegidos via Data Protection — validar rotação de keyring em HA |
| Swagger desligado em prod | Manter; expor OpenAPI assinado só em dev |

---

## 5. O que falta implementar (paridade com VMS de qualidade)

Agrupado por prioridade de produto. Itens já listados de forma solta em `pendente.md` foram consolidados e expandidos sob o ponto de vista **só de vídeo**.

### 5.1 P0 — “Dá para confiar em produção 24/7”

| Feature | Estado | Critério de pronto |
|---------|--------|--------------------|
| Auto-recuperação MediaMTX + FFmpeg com métricas | Parcial (reconcile 15 s) | Alerta + restart process + dashboard “N gravando / N falhas” |
| Testes E2E gravação → index → timeline → export | ⬜ | Pipeline CI com FFmpeg sintético |
| Pre-event buffer (10–30 s) | ⬜ | OnEvent inclui segundos **antes** do alarme |
| Relatório de gaps + alarme de stall confiável | Parcial | UI + evento `recording_gap` com duração |
| Lock de storage / cluster id | ⬜ | Impossível purgar gravações de outro ambiente por engano |
| Backup/restore de config (câmeras, layouts, regras) | ⬜ | Export JSON/ZIP versionado |
| Runbook operacional (Windows service, portas, discos) | Parcial docs | `docs/OPS-VMS.md` com checklist de go-live |

### 5.2 P1 — Experiência de operador (o que vende o posto)

| Feature | Estado | Critério de pronto |
|---------|--------|--------------------|
| Sync playback multi-câmera | ⬜ | 2–16 cams, play/pause/seek sincronizados |
| Instant replay multi-célula (não só 60 s 1 cam) | Parcial | Replay por quadrante + “todas as do layout” |
| Thumbnails na timeline | ⬜ | Strip visual; seek por clique na miniatura |
| Smart search por tipo de evento na timeline | Parcial (search API) | Filtros visuais motion/intrusion/LPR no player |
| Pop-out stage 2º monitor | ⬜ | Janela dedicada live fullscreen |
| Máscara de privacidade no live | ⬜ | Polígonos por câmera (não só blur no export) |
| Dewarp fisheye | ⬜ | Modos 360/180/panorama (cliente ou FFmpeg) |
| Teclado / joystick PTZ (protocolo gamepad/USB) | Parcial (setas) | Gamepad + velocidade variável |
| Bookmarks compartilhados + pastas de investigação | Parcial (bookmark simples) | Casos com N clips + anotações |
| Notificações push FCM/APNs | ⬜ | Mobile / PWA real |

### 5.3 P2 — Forense / compliance / prova

| Feature | Estado | Critério de pronto |
|---------|--------|--------------------|
| Cadeia de custódia do export | Parcial (.sig HMAC) | Hash SHA-256 no DB + verificação na UI + PDF laudo |
| Watermark forense (user + IP + timestamp frame) | Parcial (texto export) | Overlay opcional frame-a-frame |
| Blur seletivo (face ML) | ⬜ (boxblur global) | Detecta e borra ROI |
| Archive cold storage (S3 / NAS tiering) | ⬜ | Política: quente N dias → frio |
| Redação de áudio no export | ⬜ | Mute faixas / blur áudio |
| Relatório de retenção LGPD (o que foi apagado) | ⬜ | Log imutável de purge |

### 5.4 P3 — Escala e HA enterprise

| Feature | Estado | Critério de pronto |
|---------|--------|--------------------|
| Event bus distribuído | ⬜ in-memory | Failover de nó sem perder eventos live |
| Storage multi-volume + balanceamento | ⬜ | Novas gravações no volume com mais espaço |
| Failover de gravador sem gap perceptível | Parcial (lease 30 s) | RTO &lt; 15 s documentado e testado |
| Transcode farm / GPU | ⬜ | Offload H.265 live e normalize |
| Multi-tenant isolation forte (storage path por tenant) | Parcial (TenantId no DB) | Paths e cotas isolados |
| Discovery ONVIF em massa + bulk import | Parcial (OnvifDiscovery) | Wizard admin: scan rede → cadastrar N |

### 5.5 P4 — Analytics e valor agregado (já stubados)

| Feature | Estado | Nota |
|---------|--------|------|
| Motor facial real (embeddings / ONNX / serviço Python) | Stub fingerprint | Só com licença `AnalyticsFacial` |
| LPR com OCR real + lista permitida/bloqueada em tempo real | Cadastro de placas + eventos vendor | Regras de automação ligadas a plate match |
| Contagem de pessoas / heatmap | Eventos vendor se existirem | Sem motor próprio |
| IA generativa / análise de cena | `PlatformExtraEndpoints` stub | Integrar SpaceXAI quando for produto |

### 5.6 Drivers — fechar paridade

| Fabricante | Falta para “qualidade” |
|------------|------------------------|
| Hikvision | Edge full (SD search robusto), VCA config UI, talk estável |
| ONVIF | Profile G (gravação edge), Profile T analytics, eventos PullPoint confiável |
| Dahua / Intelbras | PTZ presets completos, playback edge, talk |
| Axis | VAPIX events + PTZ + ACAP hooks |
| Bosch / Samsung / Uniview | Hoje “stream + heartbeat”; precisam PTZ/eventos reais ou ficar marcados como **beta** na UI |

---

## 6. Matriz “MVP → Qualidade comercial”

```
                    MVP atual          Qualidade (alvo)           Enterprise
Live                ✅ WebRTC/HLS      + dewarp/máscara           + GPU multi-stream
Gravação            ✅ cont/evento     + pre-buffer               + multi-volume / RAID logic
Playback            ✅ 1 cam timeline  + multi-cam sync + thumbs  + archive cold
Export              ✅ MP4+sig         + cadeia custódia UI       + e-discovery pack
Eventos             ✅ WS+automação    + correlação cross-domain  + SOC playbooks
Drivers             ⚠️ desigual        + contrato testado/driver  + certif. ONVIF
HA                  ⚠️ lease+shard     + bus distribuído          + geo-redundância
Testes              ⚠️ unitários       + E2E mídia CI             + chaos / load 500 cams
UX posto            ✅ unificado       + modular + 2º monitor     + cliente nativo opcional
```

---

## 7. Roadmap sugerido (só VMS de qualidade)

### Onda V1 — Consolidar confiança (2–4 semanas)

1. Suite E2E: gerar RTSP sintético → gravar → indexar → timeline → export → assert tamanho/hash.  
2. Pre-event buffer configurável (`Device.PreEventSeconds` / `Vms:PreEventSeconds`).  
3. Métricas: `vms_recording_active`, `vms_segment_bytes`, `vms_export_duration`, `vms_camera_offline`.  
4. Health do MediaMTX com evento + retry de registro.  
5. Lock de storage (`cluster.uuid` no root).  
6. Documentar ops go-live e defaults seguros (`TranscodeLive=false` em prod).

### Onda V2 — Posto de operação (3–5 semanas)

1. Modularizar `monitor.html` (ou extrair Playback + Events).  
2. Playback multi-câmera sincronizado.  
3. Thumbnails de timeline (job background + endpoint).  
4. Pop-out 2º monitor.  
5. Gaps de gravação visíveis na timeline + filtro smart search polido.  
6. Admin config nativo (sair do iframe nas telas críticas de câmera).

### Onda V3 — Forense e compliance (2–3 semanas)

1. Verificação de assinatura na UI + registro de export no audit com hash.  
2. Log de purge de retenção.  
3. Melhorar crypto streaming (sem carregar arquivo inteiro).  
4. Política archive → pasta/NAS secundário.

### Onda V4 — Escala e drivers (contínuo)

1. Event bus Redis.  
2. Multi-volume storage.  
3. Contrato de testes por `IDeviceDriver` + badge “suportado / beta” no admin.  
4. Completar Dahua/Axis event+PTZ; marcar vendors rasos como experimental.  
5. Edge Profile G mais completo (busca na SD, restore seletivo).

### Onda V5 — Analytics (quando houver demanda/licença)

1. Serviço de embeddings facial real (Python/ONNX).  
2. LPR OCR + automação lista negra.  
3. Substituir stub `/ai/analyze`.

---

## 8. Critérios de aceite — “VMS de qualidade”

Um release pode ser rotulado **VMS Quality Ready** quando:

| # | Critério | Como provar |
|---|----------|-------------|
| 1 | 50 câmeras contínuas 24 h sem stall não alarado | Soak test + métricas |
| 2 | Queda de MediaMTX recupera live e gravação &lt; 60 s | Chaos kill process |
| 3 | OnEvent inclui ≥ 10 s pré-alarme | Teste com motion sintético |
| 4 | Export 1 h com watermark + .sig verificável | E2E + UI verify |
| 5 | Retenção nunca apaga bookmark/protected | Teste unitário + E2E |
| 6 | Operador sem direito não vê câmera nem stream | Testes de permissão |
| 7 | Playback multi-cam 4 canais sync ±200 ms | Teste manual / harness |
| 8 | Suite CI verde com FFmpeg (sem câmera real) | Pipeline |
| 9 | Drivers Hikvision + ONVIF + Dahua com PTZ+eventos documentados | Matriz de certificação |
| 10 | Runbook de produção revisado | `docs/OPS-VMS.md` |

> **Evidência (2026-07-26):** critérios **#1 (soak)**, **#2 (chaos recovery < 60 s)**
> e **#7 (sync ±200 ms)** agora têm harnesses executáveis em
> `tests/SecurityPlatform.Tests/Reliability/` — resultados em
> [VMS-CONFIABILIDADE.md](VMS-CONFIABILIDADE.md). Restam apenas as versões
> **sob carga real** (50 câmeras / 24 h) para o go-live.

---

## 9. Riscos conhecidos (não esquecer)

1. **Dois ambientes no mesmo `StoragePath`** → purga cruzada.  
2. **`EncryptRecordings` + perda do keyring** → gravações irrecuperáveis.  
3. **Transcode live em muitas cams** → CPU satura e derruba o All-in-One.  
4. **SQLite em multi-writer** → usar Postgres em cluster real.  
5. **Face fingerprint** pode gerar expectativa falsa de biometria.  
6. **SAML / OSDP / push** estão no backlog global, mas **não bloqueiam** qualidade do core de vídeo.  
7. **Monólito front** aumenta risco de regressão visual a cada feature.

---

## 10. Estrutura de pastas (referência rápida)

```
src/SecurityPlatform.Modules.Vms/
  VmsEndpoints.cs          # API principal vídeo
  RecorderService.cs       # Gravação
  RetentionService.cs      # Index + LGPD + encrypt
  MediaGateway.cs          # MediaMTX
  RecordingExporter.cs     # Export forense-light
  CameraHealthService.cs   # Online / stall
  EdgePullService.cs       # Buracos → SD
  AutomationEngine.cs      # IFTTT
  MapEndpoints.cs          # Sinóptico (adjacente)
  AccessControlEndpoints.cs / AlarmEndpoints.cs / FaceSearch*  # outros domínios no mesmo módulo

src/SecurityPlatform.Api/wwwroot/
  monitor.html             # Posto unificado
  admin.html               # Administração

src/SecurityPlatform.Drivers.*
  Hikvision / Onvif / Vendors / HttpIo
```

---

## 11. Conclusão

O módulo de VMS **já passou da fase de protótipo**: grava, reproduz, exporta, escala por shard, protege prova e opera em posto unificado.  

Para ser um **módulo de VMS de qualidade**, o foco deve sair de “mais endpoints” e ir para:

1. **Confiabilidade mensurável** (E2E, métricas, recovery, pre-buffer).  
2. **UX de investigação** (multi-cam sync, thumbs, gaps, 2º monitor).  
3. **Forense sério** (cadeia de custódia, crypto eficiente, purge auditável).  
4. **Drivers honestos** (badge beta vs certificado; testes de contrato).  
5. **Arquitetura multi-nó real** (bus distribuído + storage multi-volume).

Este documento deve ser a **fonte de verdade de qualidade do VMS**. O backlog tático de UI/global continua em [`pendente.md`](../pendente.md); a referência de API em [`MODULO-VMS.md`](MODULO-VMS.md).

---

*Gerado a partir da leitura do código em 2026-07-24 (serviços, endpoints, domínio, drivers, monitor, testes e configs).*

---

## 12. Confirmação de implementação (2026-07-24)

> **Status:** ondas V1–V3 e parte de V4 implementadas no código.  
> **Build:** `dotnet build` OK · **Testes:** 90 aprovados (inclui `VmsQualityWaveTests`).

### Implementado no código

| Item da auditoria | Confirmação | Onde |
|-------------------|-------------|------|
| Pre-event buffer (P0) | ✅ | `Device.PreEventSeconds`, `Vms:PreEventSeconds`, prefixo `p_`, promoção a `event` + protect em `RecorderService` |
| Métricas Prometheus (P0) | ✅ | `VmsMetrics` → `/metrics` (`vms_recording_active`, gaps, exports, media gateway…) |
| Health MediaMTX + recovery (P0) | ✅ | `MediaGatewayHealthService` + `media_gateway_down/up` + re-registro de paths |
| Lock `cluster.uuid` (P0) | ✅ | `StorageClusterLock` no boot; aborta se ClusterId divergir |
| E2E FFmpeg suite (P0) | ✅ | `tests/.../VmsQualityWaveTests.cs` (record/index/export path + crypto + cluster) |
| Defaults seguros (P0) | ✅ | `TranscodeLive: false` em `appsettings.json` |
| Runbook ops (P0) | ✅ | [`docs/OPS-VMS.md`](OPS-VMS.md) |
| Gaps na timeline + evento (P1) | ✅ | `timeline.gaps` + UI hachurada; `recording_gap` em `CameraHealthService` |
| Thumbnails timeline (P1) | ✅ | `ThumbnailService` + `GET .../thumbs/{stamp}` + strip no monitor |
| Sync multi-cam + pop-out (P1) | ✅ | `abrirSyncPlayback()` / `popOutStage()` no `monitor.html` |
| Cadeia de custódia export (P2) | ✅ | `ExportRecord` + SHA-256 + `POST /export/verify` + headers `X-Export-*` |
| Log de purge LGPD (P2) | ✅ | `RetentionPurgeLog` + `GET /retention/purge-log` |
| Crypto streaming (P2) | ✅ | `RecordingCrypto` v2 framed + cache `.plain.cache` (sem carregar MP4 inteiro) |
| Archive frio (P2) | ✅ | `ArchiveService` + `SystemSettings.ArchivePath` / `ArchiveAfterDays` |
| Multi-volume storage (P3) | ✅ | `Vms:StorageVolumes` + `StoragePaths.PickVolume` |
| Badge maturidade drivers (P3) | ✅ | `DriverRegistry.List()` com maturity/stream/ptz/events |
| Máscara privacidade (API) | ✅ | `PrivacyMask` + GET/PUT `/cameras/{id}/privacy-masks` |
| Migration schema | ✅ | `20260724210000_WaveVmsQuality` (SQLite + Postgres) |

### Continuação (Redis + máscara live) — implementado

| Item | Confirmação | Onde |
|------|-------------|------|
| Event bus Redis real | ✅ | `RedisEventBus` (StackExchange.Redis); `Vms:EventBus=redis://…`; fallback local se Redis cair |
| Máscara privacidade no **live** | ✅ | SVG `.privacy-layer` no quadrante; carrega `/privacy-masks`; menu “Máscara privacidade…” |
| Máscara no **export** | ✅ | FFmpeg `drawbox` a partir dos polígonos (`ExportOptions.PrivacyBoxes`) |

### Ainda aberto (explícito)

| Item | Situação |
|------|----------|
| Modularização completa do `monitor.html` | Monólito com funções novas; sem bundles ES modules |
| Admin nativo sem iframe | Ainda iframe |
| Dewarp fisheye | ⬜ |
| Facial/LPR/IA reais | Stub/fingerprint (Onda V5) |
| Completar drivers Bosch/Samsung/Uniview | Badge **experimental** |
| Soak / chaos MediaMTX / sync multi-cam | 🟢 harnesses em `tests/.../Reliability/` — ver [VMS-CONFIABILIDADE.md](VMS-CONFIABILIDADE.md). Falta só soak/chaos **sob carga real** no go-live |

### Como validar rápido

```text
dotnet test tests/SecurityPlatform.Tests
# subir API + MediaMTX
# GET /metrics → vms_*
# StoragePath/cluster.uuid criado no boot
# OnEvent + motion → segmentos p_* depois e_*
# Export → X-Export-Sha256 + POST /api/vms/export/verify
# Timeline → gaps + thumbs (após ThumbnailService rodar)
```
