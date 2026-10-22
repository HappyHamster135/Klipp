// Local QA entry only. Exercise the real worker on devices without a native AAC encoder.
// This is never imported by index.html or included in the production build.
const encoder = globalThis.AudioEncoder;
// The page may post its request while the dynamic import is still loading.
const pending: MessageEvent[] = [];
self.onmessage = event => { pending.push(event); };
if (encoder) Object.defineProperty(encoder, 'isConfigSupported', {
  configurable: true,
  value: async (config: AudioEncoderConfig) => ({supported: false, config}),
});
await import('../src/media/prepare.worker');
for (const event of pending) self.dispatchEvent(event);
export {};
