"use strict";
(() => {
  const el = id => document.getElementById(id);
  const token = location.hash.slice(1);
  let context, worklet, gain, socket, heartbeat, retryTimer;
  let running = false, generation = 0, retries = 0, connected = false, received = 0, lastPacketAt = 0;
  let lastStats = {bufferMs: 0, underruns: 0, gaps: 0, resets: 0};
  const status = (text, live = false) => { el("status").textContent = text; el("dot").classList.toggle("live", live); };
  const volume = () => {
    const value = Number(el("volume").value) / 100;
    el("volumeValue").textContent = Math.round(value * 100) + "%";
    if (gain && context) gain.gain.setTargetAtTime(value, context.currentTime, .015);
  };
  function updateMetrics() {
    el("metrics").textContent = `已接收 ${(received / 1024 / 1024).toFixed(2)} MB · 播放队列 ${Math.round(lastStats.bufferMs)} ms · 缓冲耗尽 ${lastStats.underruns} 次 · 序号中断 ${lastStats.gaps} 次 · 丢弃过期队列 ${lastStats.resets} 次。统计只反映本页面。`;
  }
  function connect(epoch) {
    if (!running || epoch !== generation) return;
    connected = false;
    status(retries ? `连接中断，正在重连（${retries}/5）…` : "正在连接音乐分享…");
    const ws = socket = new WebSocket(`${location.protocol === "https:" ? "wss:" : "ws:"}//${location.host}/audio`);
    ws.binaryType = "arraybuffer";
    ws.onopen = () => {
      if (!running || epoch !== generation) { ws.close(); return; }
      ws.send(token);
      clearInterval(heartbeat);
      lastPacketAt = Date.now();
      heartbeat = setInterval(() => {
        if (ws.readyState === WebSocket.OPEN) {
          if (connected && Date.now() - lastPacketAt > 8000) { ws.close(4000, "Audio timeout"); return; }
          ws.send("ping");
        }
      }, 5000);
    };
    ws.onmessage = event => {
      if (!running || epoch !== generation) return;
      if (typeof event.data === "string") {
        try {
          const format = JSON.parse(event.data);
          if (format.type !== "format" || format.sampleRate !== 48000 || format.channels !== 2 || format.frames !== 480 || format.format !== "float32le") throw new Error("格式不受支持");
          connected = true;
          el("source").textContent = `${format.player} · 48 kHz · 双声道 · 未压缩 PCM`;
          status("已连接，正在缓冲…");
        } catch { el("notice").textContent = "分享端音频格式不受支持，请双方更新 VoiceBridge。"; void stop(); }
      } else if (connected && event.data instanceof ArrayBuffer) {
        lastPacketAt = Date.now();
        received += event.data.byteLength;
        worklet.port.postMessage({type: "packet", data: event.data}, [event.data]);
      }
    };
    ws.onerror = () => { /* onclose provides the retry decision. */ };
    ws.onclose = event => {
      if (!running || epoch !== generation) return;
      clearInterval(heartbeat);
      connected = false;
      worklet.port.postMessage({type: "reset"});
      if (event.code === 1008 || retries >= 5) {
        void stop();
        status(event.code === 1008 ? "收听密钥无效、已失效或收听人数已满" : "连接已中断，请确认分享者仍在分享后重新收听");
        return;
      }
      retries++;
      status(`连接中断，稍后重连（${retries}/5）…`);
      retryTimer = setTimeout(() => connect(epoch), Math.min(10000, 1000 * 2 ** (retries - 1)));
    };
  }
  async function start() {
    if (running) { if (context?.state === "suspended") await context.resume(); return; }
    if (!/^[a-f0-9]{64}$/.test(token)) { status("缺少有效收听密钥，请打开分享者发送的完整链接"); return; }
    if (!window.isSecureContext || !window.AudioWorkletNode) { status("此浏览器需要 HTTPS 安全链接及 AudioWorklet 支持，请用新版 Edge / Chrome"); return; }
    running = true; const epoch = ++generation;
    el("start").disabled = true; el("stop").disabled = false;
    try {
      const audioContext = new AudioContext({sampleRate: 48000, latencyHint: "playback"});
      context = audioContext;
      await audioContext.resume(); // Must be called from the user's click gesture.
      if (!running || epoch !== generation) return;
      await audioContext.audioWorklet.addModule("/pcm-worklet.js");
      if (!running || epoch !== generation) return;
      worklet = new AudioWorkletNode(context, "voicebridge-pcm", {numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2], processorOptions: {bufferMs: Number(el("buffer").value)}});
      gain = context.createGain(); gain.gain.value = Number(el("volume").value) / 100;
      worklet.connect(gain).connect(context.destination);
      worklet.port.onmessage = event => {
        if (!running || epoch !== generation) return;
        lastStats = event.data;
        el("level").style.width = Math.min(100, lastStats.peak * Number(el("volume").value)) + "%";
        if (connected) {
          retries = 0;
          status(context.state !== "running" ? "浏览器已暂停音频，请点击继续收听" : lastStats.playing ? "正在收听" : "已连接，正在缓冲…", context.state === "running" && lastStats.playing);
        }
        updateMetrics();
      };
      context.onstatechange = () => {
        if (running && context?.state === "suspended") { el("start").textContent = "继续收听"; el("start").disabled = false; status("浏览器已暂停音频，请点击继续收听"); }
        else if (running) el("start").disabled = true;
      };
      received = 0; retries = 0; connect(epoch);
    } catch {
      if (epoch === generation) { await stop(); status("无法开启播放，请检查浏览器支持和声音输出后重试"); }
    }
  }
  async function stop() {
    running = false; connected = false; generation++;
    clearInterval(heartbeat); clearTimeout(retryTimer);
    const oldSocket = socket; socket = null;
    if (oldSocket) { oldSocket.onclose = null; oldSocket.close(); }
    worklet?.disconnect(); gain?.disconnect(); worklet = gain = null;
    const oldContext = context; context = null;
    el("start").textContent = "开始收听"; el("start").disabled = false; el("stop").disabled = true;
    el("level").style.width = "0";
    status("已停止收听");
    if (oldContext) { oldContext.onstatechange = null; await oldContext.close().catch(() => {}); }
  }
  el("start").addEventListener("click", () => void start());
  el("stop").addEventListener("click", () => void stop());
  el("volume").addEventListener("input", volume);
  el("buffer").addEventListener("change", () => worklet?.port.postMessage({type: "configure", bufferMs: Number(el("buffer").value)}));
  window.addEventListener("pagehide", () => { void stop(); });
})();
