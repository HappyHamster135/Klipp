import { describe, expect, it } from 'vitest';
import { planAudioConversion } from './audio-plan';

describe('share-copy audio planning', () => {
  it('keeps audio disabled even when the device can encode it', () => {
    expect(planAudioConversion(false, 'aac', true)).toEqual({ mode: 'discard' });
    expect(planAudioConversion(false, 'opus', false)).toEqual({ mode: 'discard' });
  });

  it('does not invent a track for silent video', () => {
    expect(planAudioConversion(true, null, true)).toEqual({ mode: 'discard' });
    expect(planAudioConversion(true, null, false)).toEqual({ mode: 'discard' });
  });

  it('uses native transcoding for precise edits when supported', () => {
    expect(planAudioConversion(true, 'aac', true)).toEqual({ mode: 'native' });
    expect(planAudioConversion(true, 'opus', true)).toEqual({ mode: 'native' });
  });

  it('copies AAC without transformations or any media outside the chosen range', () => {
    expect(planAudioConversion(true, 'aac', false)).toEqual({
      mode: 'copy',
      copy: { boundaryPolicy: 'shrink', shiftTolerance: 0 },
      audio: { codec: 'aac' },
    });
  });

  it.each(['opus', 'mp3', 'flac'] as const)('explains alternatives for unsupported %s conversion', codec => {
    expect(() => planAudioConversion(true, codec, false)).toThrow('Choose an H.264/AAC MP4, turn audio off, or upload through Klipp on your PC.');
  });
});
