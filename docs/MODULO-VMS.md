# Módulo VMS — Referência

Gravação, playback, exportação, PTZ e eventos de vídeo.
Código em `src/SecurityPlatform.Modules.Vms/`.

> **Manual prático (como fazer cada situação):** [`MANUAL-VMS-FUNCIONALIDADES.md`](MANUAL-VMS-FUNCIONALIDADES.md)

---

## 1. Serviços em segundo plano

| Serviço | Ciclo | O que faz |
|---------|-------|-----------|
| `MediaSyncService` | contínuo | mantém os paths do MediaMTX alinhados com o banco |
| `DeviceEventListener` | 30 s | assina eventos nativos (ISAPI, ONVIF) por dispositivo |
| `RecorderService` | 15 s | um FFmpeg por câmera; contínuo ou por evento |
| `RetentionService` | 5 min | indexa segmentos, aplica prazo e cotas, protege bookmarks |

Todos respeitam **sharding** (`Vms:ShardIndex` / `Vms:ShardCount`): cada câmera
pertence a exatamente um nó. Sem isso, dois nós gravariam a mesma câmera,
duplicariam eventos no banco e disputariam a exclusão dos mesmos arquivos.

---

## 2. Modos de gravação

| Modo | Comportamento |
|------|---------------|
| `Off` | não grava |
| `Continuous` | FFmpeg sempre ativo, segmentos de `SegmentSeconds` |
| `OnEvent` | sobe o FFmpeg quando chega evento da câmera; encerra após `EventRecordSeconds` de silêncio |

No modo `OnEvent` o gravador assina o barramento de eventos diretamente — não
espera o ciclo de 15 s da reconciliação, porque isso atrasaria o início da
gravação de um alarme. Como o RTSP já está publicado no gateway de mídia, a
latência até começar a gravar fica em décimos de segundo.

### Nome dos arquivos

```
{StoragePath}/{deviceId}/c_20260722_143000.mp4   ← contínuo
{StoragePath}/{deviceId}/e_20260722_143512.mp4   ← por evento
```

O prefixo identifica a origem e o carimbo (`-strftime` do FFmpeg) fornece o
`StartedAt`. O `RetentionService` lê o instante **do nome**, não de
`FileInfo.CreationTimeUtc` — este último não é confiável em volume montado nem
no Linux, e um `StartedAt` errado desalinha a linha do tempo do playback e faz o
export recortar o trecho errado. Arquivos sem prefixo (formato antigo) caem no
carimbo do sistema de arquivos.

---

## 3. Retenção

Três limites, aplicados nesta ordem:

1. **Prazo** — `Device.RetentionDays` (LGPD: descarte automático).
2. **Cota por câmera** — `Device.MaxStorageGb`; remove o mais antigo até caber.
3. **Cota global** — `Vms:MaxStorageGb`; varre todas as câmeras, mais antigo primeiro.

Gravação **protegida** (coberta por um bookmark) nunca é apagada
automaticamente. Se só restarem gravações protegidas e a cota global continuar
estourada, o serviço registra erro em log em vez de apagar prova.

> ⚠️ **Nunca aponte duas instâncias para o mesmo `StoragePath` com bancos
> diferentes.** O `RetentionService` varre o disco, indexa o que encontra no
> **seu** banco e apaga o que passou do prazo daquele banco. Uma instância de
> teste ou de desenvolvimento com um banco vazio vai indexar as gravações de
> produção como se fossem dela e purgar as que excederem a retenção padrão.
> Para vários nós sobre o mesmo volume, use **sharding** (`ShardIndex` /
> `ShardCount`) com o **mesmo banco** — foi para isso que ele existe.
> Em teste, isole também `ExportPath`.

---

## 4. API

Base: `/api/vms` — exige autenticação; direitos verificados por câmera.

### Câmeras

| Método | Rota | Permissão |
|--------|------|-----------|
| `GET` | `/cameras` | filtra pelas visíveis |
| `POST` | `/cameras` | `camera.config` |
| `DELETE` | `/cameras/{id}?keepFiles=false` | `camera.config` |
| `GET` | `/cameras/{id}/stream` | `camera.view` |
| `GET` | `/cameras/{id}/snapshot` | `camera.view` |
| `GET` | `/cameras/health` | snapshot online/offline + FPS/bitrate |
| `GET`/`POST`/`DELETE` | `/layouts` | mosaicos do monitor (por usuário) |
| `POST` | `/cameras/{id}/ptz/tour/start\|stop` | patrulha de presets |
| `GET` | `/cameras/{id}/search?type=&from=&to=` | busca smart (eventos + gravação) |
| `GET` | `/cameras/{id}/timeline` | blocos + bookmarks + **eventos** |
| `POST` | `/cameras/{id}/talk` / `talk/open` / `talk/close` | áudio bidirecional |
| — | Edge pull / HA / transcode | `Vms:HaEnabled`, `TranscodeLive`, `Device.EdgePullEnabled` |

`DELETE` remove em cascata gravações, arquivos, bookmarks, agendamentos,
associações a grupo e direitos apontando para a câmera. `keepFiles=true`
preserva os arquivos em disco. A resposta informa quantas gravações protegidas
foram descartadas.

`snapshot` tenta o protocolo nativo primeiro; se a câmera não expõe o recurso,
extrai um quadro do próprio RTSP via FFmpeg — funciona em qualquer câmera.

### PTZ

| Método | Rota | Permissão |
|--------|------|-----------|
| `POST` | `/cameras/{id}/ptz/move` | `camera.ptz` |
| `POST` | `/cameras/{id}/ptz/stop` | `camera.ptz` |
| `GET` | `/cameras/{id}/ptz/presets` | `camera.view` |
| `PUT` | `/cameras/{id}/ptz/presets/{preset}` | `camera.ptz` |
| `POST` | `/cameras/{id}/command/{action}` | `camera.ptz` se `ptz*`, senão `camera.view` |

Velocidade **normalizada em -1..1** nos três eixos; cada driver converte para a
escala do fabricante (o Hikvision usa -100..100). Normalizar na plataforma é o
que permite ao cliente falar com qualquer câmera do mesmo jeito.

```jsonc
POST /api/vms/cameras/7/ptz/move
{ "pan": 0.5, "tilt": -0.2, "zoom": 0, "timeoutSeconds": 2 }
```

### Playback e exportação

| Método | Rota | Permissão |
|--------|------|-----------|
| `GET` | `/cameras/{id}/recordings?from=&to=&page=&pageSize=` | `camera.playback` |
| `GET` | `/cameras/{id}/timeline?from=&to=` | `camera.playback` |
| `GET` | `/recordings/{id}/file` | `camera.playback` |
| `POST` | `/cameras/{id}/export` | `camera.export` |

`/recordings` devolve `{ total, page, pageSize, itens }` — paginação real, em vez
do `Take(500)` fixo que escondia gravação antiga sem qualquer sinal.

`/timeline` agrupa segmentos em blocos contínuos e expõe os buracos. Segmentos
separados por menos de 30 s são o corte normal do gravador, não interrupção — a
junção evita uma timeline picotada. Sem isso, o operador não distingue "nada
aconteceu" de "não gravou".

`/export` junta os segmentos que cobrem o intervalo em um MP4 único, com
`-c copy` (sem reencode: exportar uma hora leva segundos e preserva a qualidade
original, o que importa quando o vídeo vira prova). Teto configurável em
`Vms:MaxExportMinutes`.

### Bookmarks

| Método | Rota | Permissão |
|--------|------|-----------|
| `GET` | `/cameras/{id}/bookmarks` | `camera.playback` |
| `POST` | `/cameras/{id}/bookmarks` | `camera.playback` |
| `DELETE` | `/bookmarks/{id}` | `camera.playback` |

Criar um bookmark protege **na hora** as gravações do intervalo. Esperar a
próxima passada da retenção deixaria uma janela em que o arquivo ainda pode ser
apagado.

### Eventos

| Método | Rota | Permissão |
|--------|------|-----------|
| `POST` | `/events` | `event.ack` na câmera de origem |
| `GET` | `/events?deviceId=&type=&from=&unacknowledged=` | filtra pelas visíveis |
| `POST` | `/events/{id}/ack` | `event.ack` |

`POST /events` recebe um DTO restrito (`type`, `deviceId`, `severity`,
`payload`). `Id` e `TenantId` vêm do servidor — antes, qualquer usuário
autenticado podia injetar evento arbitrário escolhendo os dois.

---

## 5. Configuração (`Vms`)

| Chave | Padrão | Descrição |
|-------|--------|-----------|
| `MediaMtxApi` | `http://localhost:9997` | API de controle do MediaMTX |
| `MediaPublicHost` | `http://localhost` | host visto pelo navegador |
| `HlsPort` / `WebRtcPort` | `8888` / `8889` | portas do nó de mídia |
| `StoragePath` | `./data/recordings` | raiz das gravações |
| `ExportPath` | `./data/exports` | vídeos exportados |
| `FfmpegPath` | `ffmpeg` | binário do FFmpeg |
| `SegmentSeconds` | `600` | duração de cada segmento |
| `MaxExportMinutes` | `60` | teto de um export |
| `MaxStorageGb` | `0` | cota global; 0 = sem limite |
| `RecorderEnabled` | `true` | desliga a gravação neste nó |
| `ShardIndex` / `ShardCount` | `0` / `1` | fatia de câmeras deste nó |

---

## 6. Correções aplicadas

| # | Falha | Correção |
|---|-------|----------|
| 1 | `RecordingMode.OnEvent` existia no enum mas nunca gravava | gravação por evento implementada, disparada pelo barramento |
| 2 | Excluir câmera deixava gravações e arquivos órfãos | cascata completa + remoção do diretório |
| 3 | `POST /events` aceitava `DeviceEvent` cru de qualquer usuário | DTO restrito, permissão na câmera de origem, tenant do token |
| 4 | `TenantId` vinha do corpo da requisição | vem do token (`ClaimsPrincipal.TenantId()`) |
| 5 | `DeviceEventListener` sem sharding | duplicação de evento eliminada |
| 6 | `RetentionService` sem sharding | corrida na exclusão eliminada |
| 7 | `StartedAt` de `FileInfo.CreationTimeUtc` | lido do nome do arquivo |
| 8 | Nenhum teste | 23 testes (permissões, parsing, confinamento de caminho, sharding) |

---

## 7. O que ainda não está implementado

> Lista viva consolidada em [`pendente.md`](../pendente.md) na raiz do repositório.  
> **Auditoria de qualidade / gaps para VMS comercial:** [`VMS-AUDITORIA-QUALIDADE.md`](VMS-AUDITORIA-QUALIDADE.md).

| Item | Situação | Nota |
|------|----------|------|
| **PTZ no driver ONVIF** | só o driver Hikvision tem PTZ | ONVIF exige o serviço SOAP PTZ; o endpoint devolve erro explicativo apontando o driver nativo |
| **Presets no ONVIF** | idem | mesma causa |
| **Criptografia de gravação** | `SystemSettings.EncryptRecordings` não tem efeito | arquivos ficam em claro no disco |
| **Bitrate/FPS em tempo real** | saúde por câmera cobre silêncio e online | não há amostragem de bitrate/FPS do FFmpeg |
| **Testes de integração** | só testes unitários | gravação e export não são exercitados de ponta a ponta (dependem de FFmpeg e câmera real) |

### Implementado nesta rodada

| Item | Onde |
|------|------|
| Agendamento de gravação (`ScheduleSlot`) | `RecordingSchedule` + `RecorderService` |
| Perfis de mídia na URL RTSP | `Device.RecordingProfileId` / `LiveProfileId` + `StreamUrlBuilder` |
| Watermark na exportação | `RecordingExporter` + `SystemSettings.WatermarkExport` |
| Áudio na gravação | `Device.RecordAudio` + `VmsOptions.RecordAudio` |
| Saúde por câmera + `recording_stalled` | `CameraHealthService`, `GET /api/vms/cameras/health` |
| Motor de automação | `AutomationEngine` (Email, PTZ, Bookmark, HttpRequest) |
| Licença bloqueando canal excedente | `POST /api/vms/cameras` retorna 409 |
| Migrations EF | `DeviceMediaProfilesAndAudio` (SQLite + Postgres) |
