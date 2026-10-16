"use strict";
class VoiceBridgePcm extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.capacity = 192000; this.left = new Float32Array(this.capacity); this.right = new Float32Array(this.capacity);
    this.read = 0; this.write = 0; this.size = 0; this.fraction = 0; this.playing = false; this.fade = 0;
    this.target = 48000 * (options.processorOptions.bufferMs || 1200) / 1000;
    this.underruns = this.gaps = this.resets = this.report = 0; this.sequence = null;
    this.port.onmessage = event => {
      const message = event.data;
      if (message.type === "reset") { this.clear(); this.sequence = null; }
      else if (message.type === "configure") { this.target = 48000 * Math.max(600, Math.min(2400, Number(message.bufferMs))) / 1000; this.clear(); }
      else if (message.type === "packet") this.packet(message.data);
    };
  }
  clear() { this.read = this.write = this.size = this.fraction = this.fade = 0; this.playing = false; }
  packet(data) {
    if (!(data instanceof ArrayBuffer) || data.byteLength !== 3856) return;
    const view = new DataView(data);
    if (view.getUint32(0, true) !== 0x314d4256 || view.getUint32(8, true) !== 48000 || view.getUint16(12, true) !== 2 || view.getUint16(14, true) !== 480) return;
    const sequence = view.getUint32(4, true);
    if (this.sequence !== null && sequence !== ((this.sequence + 1) >>> 0)) { this.gaps++; this.clear(); }
    this.sequence = sequence;
    // Limit real latency even when a backgrounded browser receives a stale TCP burst.
    if (this.size + 480 > this.target + 24000) { this.resets++; this.clear(); }
    for (let i = 0; i < 480; i++) {
      const l = view.getFloat32(16 + i * 8, true), r = view.getFloat32(20 + i * 8, true);
      this.left[this.write] = Number.isFinite(l) ? Math.max(-1, Math.min(1, l)) : 0;
      this.right[this.write] = Number.isFinite(r) ? Math.max(-1, Math.min(1, r)) : 0;
      this.write = (this.write + 1) % this.capacity; this.size++;
    }
  }
  process(inputs, outputs) {
    const l = outputs[0][0], r = outputs[0][1]; let peak = 0;
    if (!this.playing && this.size >= this.target) { this.playing = true; this.fade = 0; }
    // Slowly follow sender/receiver clock drift. No signal-dependent gain or compression.
    const correction = Math.max(-.002, Math.min(.002, (this.size - this.target) / 480000));
    const step = (48000 / sampleRate) * (1 + correction);
    for (let i = 0; i < l.length; i++) {
      if (this.playing && this.size >= 2) {
        const next = (this.read + 1) % this.capacity;
        this.fade = Math.min(1, this.fade + 1 / 240);
        const amplitude = this.fade * Math.min(1, this.size / 240);
        l[i] = (this.left[this.read] + (this.left[next] - this.left[this.read]) * this.fraction) * amplitude;
        r[i] = (this.right[this.read] + (this.right[next] - this.right[this.read]) * this.fraction) * amplitude;
        this.fraction += step;
        const advance = Math.min(this.size, Math.floor(this.fraction));
        this.read = (this.read + advance) % this.capacity; this.size -= advance; this.fraction -= advance;
        peak = Math.max(peak, Math.abs(l[i]), Math.abs(r[i]));
      } else {
        if (this.playing) { this.playing = false; this.underruns++; this.fade = 0; }
        l[i] = r[i] = 0;
      }
    }
    this.report += l.length;
    if (this.report >= sampleRate / 4) {
      this.report = 0;
      this.port.postMessage({bufferMs: this.size / 48, playing: this.playing, peak, underruns: this.underruns, gaps: this.gaps, resets: this.resets});
    }
    return true;
  }
}
registerProcessor("voicebridge-pcm", VoiceBridgePcm);
