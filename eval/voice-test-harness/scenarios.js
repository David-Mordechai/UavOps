// UAV-operator-style test sentences, matching the set used in the GX10 voice-eval-brief.md
// so results from this harness and the GX10 evaluation are directly comparable.
//
// `speakText` (optional) is what actually gets sent to TTS; `text` is always the ground truth
// compared against the STT transcript. These differ only for the numeric tail-number scenarios:
// feeding Chatterbox a bare compact digit string (e.g. "998") produces genuinely ambiguous
// audio - confirmed by two completely unrelated STT engines (whisper.cpp/large-v3 and NeMo
// Parakeet) independently mishearing the SAME synthesized clip in matching ways. Space-separating
// the digits before synthesis (e.g. "9 9 8") makes Chatterbox pronounce them clearly and both
// engines transcribe it perfectly - this is a TTS text-normalization fix, not an STT fix. `text`
// stays the compact form since that's the real ground truth a correctly-functioning STT should
// produce (and it's what production tail numbers actually look like) - without a separate
// `speakText`, WER scoring would otherwise compare a space-separated ground truth ("9 9 8", 3
// words) against a compact transcript ("998", 1 word) and score a *correct* transcription as a
// near-total failure.
const DEFAULT_SCENARIOS = [
  { id: "01", text: "Set 997 speed to 200 knots", speakText: "Set 9 9 7 speed to 200 knots" },
  { id: "02", text: "Return to launch" },
  { id: "03", text: "Point the payload at target alpha" },
  { id: "04", text: "What training lessons are available in the simulator" },
  { id: "05", text: "Restart the watchdog service" },
  { id: "06", text: "List the fleet" },
  { id: "07", text: "Set 998 altitude to 500 feet", speakText: "Set 9 9 8 altitude to 500 feet" },
  { id: "08", text: "Upload the waypoints for 999", speakText: "Upload the waypoints for 9 9 9" },
  { id: "09", text: "What is the link status for 997", speakText: "What is the link status for 9 9 7" },
  { id: "10", text: "Get the mission status" },
];
