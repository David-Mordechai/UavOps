// Two interchangeable provider implementations, both exposing the same shape:
//   tts.speak(text) -> Promise<void>                (resolves when audio playback finishes)
//   stt.startListening() -> void
//   stt.stopListening() -> Promise<string>           (resolves with the final transcript)
//   stt.onInterim(cb)                                (optional live partial-transcript callback)
//
// "browser" uses Chrome's built-in Web Speech API (speechSynthesis + webkitSpeechRecognition) —
// zero setup, cloud-backed recognition, a fair baseline for the "as good as Google" bar.
// "custom" posts to a configurable HTTP endpoint, so this harness can point at the GX10's
// Parakeet/CosyVoice2 containers once eval/voice-eval-brief.md's evaluation defines their API.

const BrowserProvider = {
  name: "browser",

  tts: {
    speak(text) {
      return new Promise((resolve, reject) => {
        if (!("speechSynthesis" in window)) {
          reject(new Error("speechSynthesis not supported in this browser"));
          return;
        }
        const utterance = new SpeechSynthesisUtterance(text);
        utterance.rate = 1.0;
        utterance.onend = () => resolve();
        utterance.onerror = (e) => reject(new Error("speechSynthesis error: " + e.error));
        window.speechSynthesis.cancel(); // clear any stuck queue from a prior run
        window.speechSynthesis.speak(utterance);
      });
    },
  },

  stt: (() => {
    let recognition = null;
    let finalTranscript = "";
    let interimCb = null;
    let resolveStop = null;

    function ensureRecognition() {
      const Impl = window.SpeechRecognition || window.webkitSpeechRecognition;
      if (!Impl) throw new Error("SpeechRecognition not supported in this browser");
      const r = new Impl();
      r.continuous = true;
      r.interimResults = true;
      r.lang = "en-US";
      r.onresult = (event) => {
        let interim = "";
        for (let i = event.resultIndex; i < event.results.length; i++) {
          const chunk = event.results[i][0].transcript;
          if (event.results[i].isFinal) finalTranscript += chunk + " ";
          else interim += chunk;
        }
        if (interimCb) interimCb(finalTranscript, interim);
      };
      r.onerror = (e) => {
        if (resolveStop) { resolveStop(finalTranscript.trim()); resolveStop = null; }
        console.error("[voice-test-harness] recognition error", e.error);
      };
      return r;
    }

    return {
      startListening() {
        finalTranscript = "";
        recognition = ensureRecognition();
        recognition.start();
      },
      stopListening() {
        return new Promise((resolve) => {
          if (!recognition) { resolve(""); return; }
          resolveStop = resolve;
          recognition.onend = () => {
            if (resolveStop) { resolveStop(finalTranscript.trim()); resolveStop = null; }
          };
          recognition.stop();
        });
      },
      onInterim(cb) { interimCb = cb; },
    };
  })(),
};

// Encodes mono 32-bit float PCM samples into a 16-bit PCM WAV Blob — the GX10's Parakeet
// server (server.py) picks its decode path purely from the uploaded filename's extension and
// hands the raw bytes to NeMo, so it needs a real .wav file, not MediaRecorder's webm/opus
// container (libsndfile, underneath NeMo's audio loader, doesn't speak WebM/Opus).
function encodeWav(samples, sampleRate) {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const writeStr = (offset, str) => { for (let i = 0; i < str.length; i++) view.setUint8(offset + i, str.charCodeAt(i)); };

  writeStr(0, "RIFF");
  view.setUint32(4, 36 + samples.length * 2, true);
  writeStr(8, "WAVE");
  writeStr(12, "fmt ");
  view.setUint32(16, 16, true);       // PCM fmt chunk size
  view.setUint16(20, 1, true);        // PCM format
  view.setUint16(22, 1, true);        // mono
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true); // byte rate (16-bit mono)
  view.setUint16(32, 2, true);        // block align
  view.setUint16(34, 16, true);       // bits per sample
  writeStr(36, "data");
  view.setUint32(40, samples.length * 2, true);

  let offset = 44;
  for (let i = 0; i < samples.length; i++, offset += 2) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(offset, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return new Blob([buffer], { type: "audio/wav" });
}

function makeCustomProvider(config) {
  // config: { sttUrl, ttsUrl }
  let stream = null;
  let audioCtx = null;
  let sourceNode = null;
  let processorNode = null;
  let pcmChunks = [];

  return {
    name: "custom",

    tts: {
      async speak(text) {
        const res = await fetch(config.ttsUrl, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ input: text }),
        });
        if (!res.ok) throw new Error(`TTS endpoint returned ${res.status}: ${await res.text()}`);
        const blob = await res.blob();
        const url = URL.createObjectURL(blob);
        const audio = new Audio(url);
        await new Promise((resolve, reject) => {
          audio.onended = resolve;
          audio.onerror = () => reject(new Error("audio playback failed"));
          audio.play().catch(reject);
        });
        URL.revokeObjectURL(url);
      },
    },

    stt: {
      async startListening() {
        // Chrome's default echoCancellation:true correctly identifies TTS played through this
        // same PC's speakers as "system echo" and actively suppresses it -- exactly the signal
        // this closed-loop test needs to capture. Verified empirically (waveform amplitude test,
        // 2026-09-10): near-silent capture with AEC on, strong clean signal with it off.
        stream = await navigator.mediaDevices.getUserMedia({
          audio: { echoCancellation: false, noiseSuppression: false, autoGainControl: false },
        });
        audioCtx = new AudioContext();
        sourceNode = audioCtx.createMediaStreamSource(stream);
        processorNode = audioCtx.createScriptProcessor(4096, 1, 1);
        pcmChunks = [];
        processorNode.onaudioprocess = (e) => {
          pcmChunks.push(new Float32Array(e.inputBuffer.getChannelData(0)));
        };
        sourceNode.connect(processorNode);
        processorNode.connect(audioCtx.destination); // required by some browsers to keep the graph running; silent (0-gain not needed, ScriptProcessor output is unused)
      },
      async stopListening() {
        if (!processorNode) return "";
        processorNode.disconnect();
        sourceNode.disconnect();
        const sampleRate = audioCtx.sampleRate;
        stream.getTracks().forEach((t) => t.stop());
        await audioCtx.close();

        const totalLength = pcmChunks.reduce((sum, c) => sum + c.length, 0);
        const merged = new Float32Array(totalLength);
        let offset = 0;
        for (const chunk of pcmChunks) { merged.set(chunk, offset); offset += chunk.length; }

        const wavBlob = encodeWav(merged, sampleRate);
        const form = new FormData();
        form.append("file", wavBlob, "clip.wav");
        const res = await fetch(config.sttUrl, { method: "POST", body: form });
        if (!res.ok) throw new Error(`STT endpoint returned ${res.status}: ${await res.text()}`);
        const json = await res.json();
        return (json.text || "").trim();
      },
      onInterim() { /* no live partials from a request/response HTTP endpoint */ },
    },
  };
}
