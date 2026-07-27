# Runbook operacional — Módulo VMS

> Go-live checklist e operação 24/7. Complementa [VMS-AUDITORIA-QUALIDADE.md](VMS-AUDITORIA-QUALIDADE.md).  
> **Manual de uso por situação:** [MANUAL-VMS-FUNCIONALIDADES.md](MANUAL-VMS-FUNCIONALIDADES.md).

---

## 1. Pré-requisitos

| Item | Notas |
|------|--------|
| .NET 8 Runtime / SDK | API |
| FFmpeg no `PATH` | Gravação, snapshot, export, thumbs |
| MediaMTX | `mediamtx.exe` + `mediamtx.yml` na raiz (Windows) ou sidecar |
| Disco | Volume dedicado para `Vms:StoragePath`; monitore % livre |
| Postgres | Produção multi-writer; SQLite só All-in-One / dev |

---

## 2. Defaults seguros de produção

```json
"Vms": {
  "TranscodeLive": false,
  "RecordFromMediaGateway": true,
  "SingleCameraRtspPull": true,
  "AllowDirectCameraRecord": false,
  "PreEventSeconds": 15,
  "HaEnabled": false,
  "ClusterId": "",
  "StorageVolumes": []
}
```

- **Nunca** aponte dois ambientes (dev/prod) para o mesmo `StoragePath`. O boot grava `cluster.uuid` e aborta se `ClusterId` divergir.
- Com HA: mesmo banco, mesmo `Security:KeyRingPath`, `HaEnabled=true`, shards distintos.
- HTTPS: `Security:Https:Enabled=true` com certificado.

---

## 3. Portas

| Serviço | Porta padrão |
|---------|--------------|
| API HTTP | 8080 |
| API HTTPS | 8443 |
| MediaMTX API | 9997 |
| HLS | 8888 |
| WebRTC | 8889 |
| RTSP gateway | 8554 |
| SIA UDP | 9999 (se usado) |

---

## 3b. Event bus distribuído (Redis)

All-in-One: deixe `Vms:EventBus` vazio (in-memory).

Multi-nó (mesmos eventos/automação/WS entre processos):

```json
"Vms": {
  "EventBus": "redis://localhost:6379",
  "NodeId": "gravador-1"
}
```

- Canal Pub/Sub: `sp:events`
- Se Redis cair no boot, o processo continua com fan-out **local** e loga erro
- `NodeId` evita eco do próprio publish

---

## 4. Métricas

`GET /metrics` (Prometheus text):

- `vms_recording_active`, `vms_cameras_online/offline`
- `vms_media_gateway_up`
- `vms_exports_total`, `vms_export_duration_ms_total`
- `vms_segments_indexed_total`, `vms_purge_total`
- `vms_recording_gaps_total`, `vms_preevent_promotions_total`

Eventos de saúde: `media_gateway_down` / `media_gateway_up`, `recording_stalled`, `recording_gap`.

---

## 5. Checklist go-live

1. [ ] FFmpeg e MediaMTX sobem com a API (serviço Windows / script).
2. [ ] `cluster.uuid` criado em `StoragePath`.
3. [ ] Câmera de teste: live WebRTC ou HLS &lt; 5 s.
4. [ ] Gravação contínua gera `c_*.mp4` e indexa em `/timeline`.
5. [ ] OnEvent + pre-buffer: motion gera `p_*` + promoção a `event`.
6. [ ] Export MP4 + headers `X-Export-Sha256` / `X-Export-Signature`.
7. [ ] `POST /api/vms/export/verify` valida integridade.
8. [ ] Operador sem direito não lista a câmera.
9. [ ] `/metrics` expõe `vms_*`.
10. [ ] Backup de `data/keys` (keyring) e do banco.

---

## 6. Incidentes comuns

| Sintoma | Ação |
|---------|------|
| Live offline, gravação ok | Checar MediaMTX + `media_gateway_*` |
| Gravação parada | `recording_stalled`; FFmpeg, disco, path ready |
| Export minúsculo | Intervalo sem segmentos indexados; normalizer |
| Purga “sumiu” gravação de outro site | Cluster lock / pastas separadas |
| CPU alta | `TranscodeLive=false`; reduzir canais H.265 reencode |

---

## 7. Archive frio

Em **Admin → Sistema**:

- `ArchivePath` = pasta NAS  
- `ArchiveAfterDays` &gt; 0  

O `ArchiveService` move segmentos não protegidos periodicamente.
