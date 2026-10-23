import { readFile, writeFile } from 'node:fs/promises';
const config = JSON.parse(await readFile(new URL('../../backend/community.json', import.meta.url), 'utf8').catch(() => readFile(new URL('../../../backend/community.json', import.meta.url), 'utf8')));
if (!/^https:\/\/[a-z0-9-]+\.supabase\.co$/.test(config.projectUrl) || !/^sb_publishable_[A-Za-z0-9_-]+$/.test(config.publishableKey) || Object.keys(config).some(key => !['projectUrl','publishableKey','mediaApiUrl'].includes(key))) throw new Error('Only the intended public project configuration can be copied.');
let mediaApiUrl='';
if(config.mediaApiUrl){const media=new URL(config.mediaApiUrl);if(media.protocol!=='https:'||media.pathname!=='/'||media.search||media.hash||media.username||media.password||media.hostname.endsWith('.invalid'))throw new Error('Media hosting must use a public HTTPS origin.');mediaApiUrl=media.origin;}
await writeFile(new URL('../.env.local', import.meta.url), `VITE_SUPABASE_URL=${config.projectUrl}\nVITE_SUPABASE_PUBLISHABLE_KEY=${config.publishableKey}\n${mediaApiUrl?`VITE_MEDIA_API_URL=${mediaApiUrl}\n`:''}`);
console.log('Public project configuration prepared; no server credentials copied.');
