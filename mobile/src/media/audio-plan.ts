import type { AudioCodec } from 'mediabunny';

type AudioPlan =
  | { mode: 'discard' }
  | { mode: 'native' }
  | { mode: 'copy'; copy: { boundaryPolicy: 'shrink'; shiftTolerance: 0 }; audio: { codec: 'aac' } };

/** Keep packet copy bounded to the chosen range when the device cannot encode AAC. */
export function planAudioConversion(includeAudio: boolean, sourceCodec: AudioCodec | null, nativeAac: boolean): AudioPlan {
  if (!includeAudio || sourceCodec === null) return { mode: 'discard' };
  if (nativeAac) return { mode: 'native' };
  if (sourceCodec === 'aac') {
    return { mode: 'copy', copy: { boundaryPolicy: 'shrink', shiftTolerance: 0 }, audio: { codec: 'aac' } };
  }
  throw new Error('This browser cannot convert this audio to AAC. Choose an H.264/AAC MP4, turn audio off, or upload through Klipp on your PC.');
}
