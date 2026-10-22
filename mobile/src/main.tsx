import {useEffect,useMemo,useState} from 'react';
import {createRoot} from 'react-dom/client';
import {App} from './App';
import {CommunityApi} from './lib/api';
import {createConfig} from './lib/config';
import type {CommunityConfig} from './lib/models';
import './styles.css';
function Root({config}:{config:CommunityConfig}){
  const [remember,setRemember]=useState(()=>{try{return localStorage.getItem('klipp.remember-device.v1')==='yes';}catch{return false;}});
  const api=useMemo(()=>new CommunityApi(config,{rememberDevice:remember}),[config,remember]);
  useEffect(()=>()=>api.dispose(),[api]);
  return <App api={api} remember={remember} onRemember={value=>{try{localStorage.setItem('klipp.remember-device.v1',value?'yes':'no');}catch{}setRemember(value);}}/>;
}
const root=createRoot(document.getElementById('root')!);
try{const config=createConfig(import.meta.env.VITE_SUPABASE_URL,import.meta.env.VITE_SUPABASE_PUBLISHABLE_KEY);root.render(<Root config={config}/>);}catch{root.render(<main className="empty"><h1>Media is getting ready</h1><p>The mobile app needs its public community configuration before sign-in is available.</p></main>);}
if(import.meta.env.PROD && 'serviceWorker' in navigator)window.addEventListener('load',()=>{void navigator.serviceWorker.register(`${import.meta.env.BASE_URL}sw.js`).catch(()=>{});});
