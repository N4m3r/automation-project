# Pendências — Plataforma Unificada de Segurança

> Atualizado em **2026-07-24** — **Onda F entregue: Posto unificado** (`monitor.html` = app único).

---

## Legenda

| Símbolo | Significado |
|---------|-------------|
| 🟢 | Feito |
| ⚠️ | Parcial / simplificado |
| ⬜ | Ainda aberto |

---

## 1. Posto unificado (Onda F) — **implementado**

Uma URL principal: **`/monitor.html`**

| Aba | Conteúdo |
|-----|----------|
| **Live** | Mosaicos, WebRTC/HLS, PTZ, talk-back, tour, bookmark, replay 60s, health HUD |
| **Eventos** | Fila de alarmes (ack/resolve) + eventos de vídeo (filtro, live, ack) |
| **Mapa** | Mapas + marcadores embutidos (clique / duplo → live ou playback) |
| **Acesso** | Portas (destravar / credencial / fechar), presença, visitantes |
| **Export** | Intervalo + MP4 no posto (sem página separada) |
| **Config** | Admin embutido em iframe (só admin) |

### Redirects (legado)

| URL antiga | Vai para |
|------------|----------|
| `/` (após login) | `/monitor.html` |
| `/map.html` | `/monitor.html?view=map` |
| `/export.html` | `/monitor.html?view=export` |
| `/mobile.html` | `/monitor.html` (PWA start_url também) |
| SSO OIDC/SAML | `/monitor.html` |

Portal com `?stay=1` ainda mostra o launcher.

### Extras no live

- Tour PTZ ▶/⏹  
- Bookmark ±30s  
- Instant replay 60s (**I**)  
- Snapshot servidor + foto de tela  
- HUD REC / FPS / bitrate (poll health)  
- Alarme sonoro + Notification na queda / sev≥3  
- Teclado PTZ (setas, +/-)  
- Deep-link `?view=events|map|access|export|config` e `?cam=&action=`  

---

## 2. Ainda aberto (backlog global)

> **VMS em profundidade + confirmação de implementação:**  
> [`docs/VMS-AUDITORIA-QUALIDADE.md`](docs/VMS-AUDITORIA-QUALIDADE.md) §12

| Item | Nota |
|------|------|
| Admin full nativo (sem iframe) | Config usa iframe de `admin.html` |
| Edição rica de mapa no posto | Criar/editar mapa ainda no admin |
| OSDP nativo | SCA = HTTP-IO |
| SAML com validação de assinatura | |
| Blur LGPD com detecção de face ML | boxblur global no export |
| IA real / embeddings | stub |
| Push FCM/APNs | |
| Sync playback multi-câmera | 🟢 base + **relógio mestre `SyncClock` ±200 ms** (malha de deriva no posto; testado — `Reliability/VmsSyncClockTests`) |
| Pop-out stage 2º monitor | 🟢 base (`Pop-out stage`) |
| Confiabilidade provada (soak/chaos/sync) | 🟢 harnesses `tests/.../Reliability/` — ver `docs/VMS-CONFIABILIDADE.md` |
| Dewarp fisheye | ⬜ |
| Máscara privacidade **live** + export | 🟢 SVG overlay + drawbox no export |
| Pre-event buffer (gravação OnEvent) | 🟢 implementado |
| E2E gravação/timeline/export no CI | 🟢 `VmsQualityWaveTests` |
| Event bus distribuído (multi-nó) | 🟢 Redis via `Vms:EventBus=redis://…` |
| Thumbnails na timeline | 🟢 `ThumbnailService` |
| Crypto streaming + purge log + export SHA | 🟢 |

---

## 3. Como validar

1. Login em `/` → deve cair no posto.  
2. Abas Live / Eventos / Mapa / Acesso / Export / Config.  
3. Mapa: selecionar mapa → duplo clique marcador → live 1×1.  
4. Acesso: destravar porta.  
5. Eventos: ack alarme.  
6. Export: escolher câmera + intervalo → MP4.  
7. Live: **I** replay, **B** bookmark, setas PTZ.  

---

*Fonte da verdade: este arquivo.*
