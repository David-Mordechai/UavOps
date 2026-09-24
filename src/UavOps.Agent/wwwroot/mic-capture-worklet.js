// AudioWorklet half of chat.js's mic capture (replaces the deprecated ScriptProcessorNode). Runs on
// the audio rendering thread: collects the mic's mono Float32 samples and posts them to the page in
// 4096-sample batches - the same chunk size the ScriptProcessorNode used - rather than once per
// 128-sample render quantum. On "flush" it posts whatever is left, then "flushed", so the page
// knows every sample up to the moment it stopped has arrived (port messages are delivered in
// order) before it builds the WAV.
const BATCH_SIZE = 4096;

class MicCaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.buffer = new Float32Array(BATCH_SIZE);
    this.filled = 0;
    this.port.onmessage = (e) => {
      if (e.data !== "flush") return;
      if (this.filled > 0) this.port.postMessage(this.buffer.slice(0, this.filled));
      this.filled = 0;
      this.port.postMessage("flushed");
    };
  }

  process(inputs) {
    const channel = inputs[0] && inputs[0][0];
    if (channel) {
      let read = 0;
      while (read < channel.length) {
        const count = Math.min(channel.length - read, BATCH_SIZE - this.filled);
        this.buffer.set(channel.subarray(read, read + count), this.filled);
        this.filled += count;
        read += count;
        if (this.filled === BATCH_SIZE) {
          this.port.postMessage(this.buffer.slice());
          this.filled = 0;
        }
      }
    }
    return true; // keep running until the page disconnects the node
  }
}

registerProcessor("mic-capture", MicCaptureProcessor);
