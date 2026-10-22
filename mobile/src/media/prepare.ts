export interface ClipInfo { duration: number; width: number; height: number; hasAudio: boolean; posters: Blob[] }
export interface ShareOptions { start: number; end: number; includeAudio: boolean }
export interface PreparedClip { video: Blob; poster: Blob; durationSeconds: number }
export {validRange,outputDimensions} from './range';
function runWorker<T>(file: File, action: 'probe' | 'prepare', options?: ShareOptions, signal?: AbortSignal, onProgress?: (value: number) => void): Promise<T> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) { reject(new DOMException('Canceled', 'AbortError')); return; }
    const worker = new Worker(new URL('./prepare.worker.ts', import.meta.url), { type: 'module' });
    const finish = () => { worker.terminate(); clearTimeout(timeout); signal?.removeEventListener('abort', abort); };
    const abort = () => { finish(); reject(new DOMException('Canceled', 'AbortError')); };
    const timeout = setTimeout(() => { finish(); reject(new Error('Preparing the clip took too long. Choose a shorter section and try again.')); }, action === 'probe' ? 60000 : 300000);
    signal?.addEventListener('abort', abort, { once: true });
    worker.onmessage = ({data}) => {
      if (data.type === 'progress') onProgress?.(data.value);
      else if (data.type === 'done') { finish(); resolve(data.result); }
      else if (data.type === 'error') { finish(); reject(new Error(data.message)); }
    };
    worker.onerror = event => { event.preventDefault(); finish(); reject(new Error('This browser could not prepare the clip. Try the latest Safari or Chrome.')); };
    worker.postMessage({ file, action, options });
  });
}
export const inspectClip = (file: File, signal?: AbortSignal) => runWorker<ClipInfo>(file, 'probe', undefined, signal);
export const prepareClip = (file: File, options: ShareOptions, signal?: AbortSignal, onProgress?: (value:number)=>void) => runWorker<PreparedClip>(file, 'prepare', options, signal, onProgress);
