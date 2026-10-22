import { ALL_FORMATS, BlobSource, BufferTarget, CanvasSink, Conversion, Input, Mp4OutputFormat, Output, Quality, canEncodeAudio, canEncodeVideo } from 'mediabunny';
import { outputDimensions, validRange } from './range';
import { planAudioConversion } from './audio-plan';
import type {ShareOptions} from './prepare';

const jpeg = async (canvas: OffscreenCanvas | HTMLCanvasElement) => {
  if ('convertToBlob' in canvas) return canvas.convertToBlob({type:'image/jpeg',quality:0.72});
  return new Promise<Blob>((resolve,reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('Could not create thumbnail.')), 'image/jpeg',0.72));
};
self.onmessage = async ({data}: MessageEvent<{file:File;action:string;options:ShareOptions}>) => {
  let input: Input | undefined;
  try {
    if (data.file.size === 0 || data.file.size > 512 * 1024 * 1024) throw new Error('Choose a video smaller than 512 MB. You can select up to 60 seconds to share.');
    input = new Input({source:new BlobSource(data.file), formats:ALL_FORMATS});
    const videoTrack = await input.getPrimaryVideoTrack();
    if (!videoTrack) throw new Error('This file has no readable video. Choose an MP4, MOV or WebM clip.');
    const [duration,width,height,audioTrack] = await Promise.all([input.computeDuration(),videoTrack.getDisplayWidth(),videoTrack.getDisplayHeight(),input.getPrimaryAudioTrack()]);
    if (!Number.isFinite(duration) || duration <= 0) throw new Error('The video duration could not be read.');
    if (!(await videoTrack.canDecode())) throw new Error('Your browser cannot read this video format. Try exporting it as an H.264 MP4.');
    if (data.action === 'probe') {
      const sink = new CanvasSink(videoTrack,{width:160,height:90,fit:'contain',poolSize:1});
      const posters:Blob[] = [];
      for (const time of [0.01,0.2,0.4,0.6,0.8,0.98].map(part=>Math.min(duration-0.001,duration*part))) {
        const frame = await sink.getCanvas(Math.max(0,time));
        if (frame) posters.push(await jpeg(frame.canvas));
      }
      self.postMessage({type:'done',result:{duration,width,height,hasAudio:!!audioTrack,posters}});
      return;
    }
    const {start,end,includeAudio} = data.options;
    if (!validRange(start,end,duration)) throw new Error('Choose a section inside the video, up to 60 seconds.');
    const dimensions = outputDimensions(width,height);
    if (!(await canEncodeVideo('avc', {...dimensions,frameRate:30}))) throw new Error('This browser cannot create a share copy. Update Safari or Chrome, or upload through Klipp on your PC.');
    const audioQuality = new Quality({bitrate:96000});
    const audioPlan = planAudioConversion(includeAudio, audioTrack ? await audioTrack.getCodec() : null,
      !!(includeAudio && audioTrack && await canEncodeAudio('aac',{numberOfChannels:2,sampleRate:48000,quality:audioQuality})));
    const target = new BufferTarget();
    const output = new Output({format:new Mp4OutputFormat({fastStart:'in-memory'}), target});
    const conversion = await Conversion.init({input,output,tracks:'primary',copy:audioPlan.mode==='copy' ? audioPlan.copy : false,tags:{},trim:{start,end},video:{...dimensions,fit:'contain',codec:'avc',forceTranscode:true,frameRate:30,quality:new Quality({bitrate:1500000,bitrateMode:'constant'}),keyFrameInterval:2,allowTransformationMetadata:false},audio:audioPlan.mode==='native' ? {codec:'aac',forceTranscode:true,quality:audioQuality,numberOfChannels:2,sampleRate:48000} : audioPlan.mode==='copy' ? audioPlan.audio : {discard:true}});
    if (!conversion.isValid || conversion.discardedTracks.some(item=>item.track===videoTrack || (includeAudio && item.track===audioTrack))) throw new Error('This browser cannot prepare all selected tracks. Try a different clip or turn audio off.');
    conversion.onProgress = value => self.postMessage({type:'progress',value});
    await conversion.execute();
    if (!target.buffer || target.buffer.byteLength > 16*1024*1024) throw new Error('The share copy is too large. Choose a shorter section.');
    const prepared = new Blob([target.buffer],{type:'video/mp4'});
    const checked = new Input({source:new BlobSource(prepared),formats:ALL_FORMATS});
    try {
      const finalDuration = await checked.computeDuration();
      const finalVideo = await checked.getPrimaryVideoTrack();
      if (!finalVideo || finalDuration<=0 || finalDuration>60.05) throw new Error('The share copy could not be verified.');
      const frame = await new CanvasSink(finalVideo,{width:480,height:270,fit:'contain',poolSize:1}).getCanvas(Math.min(finalDuration/3,1));
      if (!frame) throw new Error('The thumbnail could not be created.');
      const poster = await jpeg(frame.canvas);
      if (poster.size>256*1024) throw new Error('The thumbnail is too large.');
      self.postMessage({type:'done',result:{video:prepared,poster,durationSeconds:Math.min(60,finalDuration)}});
    } finally { checked.dispose(); }
  } catch (error) { self.postMessage({type:'error',message:error instanceof Error ? error.message : 'The clip could not be prepared.'}); }
  finally { input?.dispose(); }
};
