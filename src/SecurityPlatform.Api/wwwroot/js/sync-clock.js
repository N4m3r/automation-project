/*
 * Relógio mestre para playback multi-câmera sincronizado (posto de operação).
 *
 * Espelha o contrato de SecurityPlatform.Modules.Vms/SyncClock.cs (fonte de
 * verdade, testada de forma determinística p/ o critério §8 #7 — 4 canais ±200 ms).
 *
 * Contrato de tempo: o instante absoluto UTC exibido por um player é
 *   startedAtMs + currentTime*1000
 * onde startedAt é o início do segmento carregado. Para sincronizar N câmeras
 * num instante mestre T, cada player recebe currentTime = (T - startedAtMs)/1000.
 */
(function (global) {
  'use strict';

  var TOLERANCE_MS = 200;

  function isFiniteNum(v) { return typeof v === 'number' && isFinite(v); }

  /** Posição (s) que o slave deve assumir p/ exibir masterAbsMs; null se fora do segmento. */
  function slaveTargetSeconds(masterAbsMs, slaveStartedAtMs, durationSec, epsilonSec) {
    epsilonSec = epsilonSec == null ? 0.05 : epsilonSec;
    if (!isFiniteNum(masterAbsMs) || !isFiniteNum(slaveStartedAtMs) ||
        !isFiniteNum(durationSec) || durationSec <= 0) return null;
    var offset = (masterAbsMs - slaveStartedAtMs) / 1000;
    if (offset < -epsilonSec) return null;
    if (offset > durationSec + epsilonSec) return null;
    return Math.min(Math.max(0, offset), Math.max(0, durationSec - epsilonSec));
  }

  function slaveAbsMs(slaveStartedAtMs, currentTimeSec) {
    return slaveStartedAtMs + currentTimeSec * 1000;
  }

  function driftMs(masterAbsMs, slaveStartedAtMs, currentTimeSec) {
    return Math.abs(slaveAbsMs(slaveStartedAtMs, currentTimeSec) - masterAbsMs);
  }

  function needsResync(drift, toleranceMs) {
    return drift > (toleranceMs == null ? TOLERANCE_MS : toleranceMs);
  }

  /**
   * Alinha uma lista de players auxiliares a masterAbsMs.
   * Cada slave: { video: HTMLVideoElement, startedAtMs: number }.
   * Re-seek só quando a deriva passa da tolerância (evita travar o vídeo).
   * Retorna a pior deriva (ms) entre os que têm imagem.
   */
  function alignPlayers(masterAbsMs, slaves, toleranceMs) {
    var worst = 0;
    for (var i = 0; i < slaves.length; i++) {
      var s = slaves[i], v = s.video;
      if (!v || !isFiniteNum(v.duration) || v.duration <= 0) continue;
      var target = slaveTargetSeconds(masterAbsMs, s.startedAtMs, v.duration);
      if (target == null) continue; // sem imagem nesse instante
      var d = driftMs(masterAbsMs, s.startedAtMs, v.currentTime);
      if (d > worst) worst = d;
      if (needsResync(d, toleranceMs)) {
        try { v.currentTime = target; } catch (e) { /* */ }
      }
    }
    return worst;
  }

  global.SyncClock = {
    TOLERANCE_MS: TOLERANCE_MS,
    slaveTargetSeconds: slaveTargetSeconds,
    slaveAbsMs: slaveAbsMs,
    driftMs: driftMs,
    needsResync: needsResync,
    alignPlayers: alignPlayers
  };
})(window);
